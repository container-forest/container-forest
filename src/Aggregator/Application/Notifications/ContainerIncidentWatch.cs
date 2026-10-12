using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Notifications;

/// <summary>
/// Picks the container events worth a notification out of the agents' Docker events:
/// <list type="bullet">
/// <item>"die" with no "stop" or "destroy" of the container within <see cref="NotificationOptions.ContainerStopWait"/>
/// before or after it is a crash, unless the container exited with code 0: a one-off job that finished. Docker reports
/// docker stop, restart and compose down as kill, stop, die; docker rm -f as kill, die, destroy; a crash as die alone.
/// The agent does not forward "kill".</item>
/// <item>"health_status: unhealthy" right away.</item>
/// </list>
/// One container gets at most one notification of a kind per <see cref="NotificationOptions.ContainerRepeatInterval"/>.
/// </summary>
/// <remarks>The state lives in memory: deaths being waited out when the aggregator stops are not notified.</remarks>
public sealed class ContainerIncidentWatch(
    IServiceScopeFactory scopeFactory,
    NotificationOptions options,
    TimeProvider time,
    ILogger<ContainerIncidentWatch> logger) : IDisposable
{
    public const string DieAction = "die";
    public const string StopAction = "stop";
    public const string DestroyAction = "destroy";
    public const string UnhealthyAction = "health_status: unhealthy";

    private readonly Lock gate = new();
    private readonly Dictionary<(EnvironmentId, string), PendingDeath> deaths = [];
    private readonly Dictionary<(EnvironmentId, string), DateTimeOffset> requestedStops = [];
    private readonly Dictionary<(EnvironmentId, string, NotificationEventKinds), DateTimeOffset> lastNotified = [];
    private readonly CancellationTokenSource stopping = new();
    private bool disposed;

    /// <summary>Takes every container event of a connected agent; most of them change nothing here.</summary>
    public void Observe(ContainerObservation observation)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            var container = (observation.EnvironmentId, observation.ContainerId);
            switch (observation.Action)
            {
                case DieAction:
                    Died(container, observation);
                    break;
                case StopAction or DestroyAction:
                    Requested(container);
                    break;
                case UnhealthyAction:
                    Raise(observation, NotificationEventKinds.ContainerUnhealthy);
                    break;
            }
        }
    }

    /// <summary>A "die": a crash unless a stop or destroy of the container comes just before or after it. Called under the lock.</summary>
    private void Died((EnvironmentId, string) container, ContainerObservation observation)
    {
        if (requestedStops.Remove(container, out var requestedAt) && time.GetUtcNow() - requestedAt < options.ContainerStopWait)
        {
            return;
        }

        if (!deaths.ContainsKey(container))
        {
            var death = new PendingDeath(observation, CancellationTokenSource.CreateLinkedTokenSource(stopping.Token));
            deaths[container] = death;
            _ = NotifyUnlessStoppedAsync(container, death);
        }
    }

    /// <summary>A "stop" or "destroy": the death being waited out, or the one about to come, was asked for. Called under the lock.</summary>
    private void Requested((EnvironmentId, string) container)
    {
        if (deaths.Remove(container, out var requested))
        {
            if (!requested.WaitOver)
            {
                requested.Waiting.Cancel();
            }

            return;
        }

        var now = time.GetUtcNow();
        foreach (var stale in requestedStops.Where(e => now - e.Value >= options.ContainerStopWait).Select(e => e.Key).ToList())
        {
            requestedStops.Remove(stale);
        }

        requestedStops[container] = now;
    }

    /// <summary>Cancels the deaths being waited out.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            stopping.Cancel();
        }
    }

    private async Task NotifyUnlessStoppedAsync((EnvironmentId, string) container, PendingDeath death)
    {
        try
        {
            await Task.Delay(options.ContainerStopWait, time, death.Waiting.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            lock (gate)
            {
                death.WaitOver = true;
                death.Waiting.Dispose();
            }
        }

        lock (gate)
        {
            // A "stop" that came as the wait ended still removed it.
            if (disposed || !deaths.TryGetValue(container, out var current) || current != death)
            {
                return;
            }

            deaths.Remove(container);
            if (death.Observation.ExitCode != 0)
            {
                Raise(death.Observation, NotificationEventKinds.ContainerDied);
            }
        }
    }

    /// <summary>Sends the notification unless one of this kind about this container went out recently. Called under the lock.</summary>
    private void Raise(ContainerObservation observation, NotificationEventKinds kind)
    {
        var now = time.GetUtcNow();

        // A recreated container keeps its name, not its id: the name is what a person recognizes as "the same".
        var key = (observation.EnvironmentId, observation.DisplayName, kind);
        if (lastNotified.TryGetValue(key, out var last) && now - last < options.ContainerRepeatInterval)
        {
            return;
        }

        foreach (var stale in lastNotified.Where(e => now - e.Value >= options.ContainerRepeatInterval).Select(e => e.Key).ToList())
        {
            lastNotified.Remove(stale);
        }

        lastNotified[key] = now;
        _ = NotifyAsync(observation, kind);
    }

    private async Task NotifyAsync(ContainerObservation observation, NotificationEventKinds kind)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ContainerIncidentNotifier>()
                .NotifyAsync(observation, kind, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The agent's connection is fine; a lost notification is only logged.
            logger.LogError(
                ex, "Cannot create {Kind} notifications for container {ContainerId} of environment {EnvironmentId}",
                kind, observation.ContainerId, observation.EnvironmentId);
        }
    }

    private sealed class PendingDeath(ContainerObservation observation, CancellationTokenSource waiting)
    {
        public ContainerObservation Observation { get; } = observation;

        /// <summary>Cancelled by the container's "stop".</summary>
        public CancellationTokenSource Waiting { get; } = waiting;

        /// <summary>Set once <see cref="Waiting"/> is disposed; there is nothing left to cancel.</summary>
        public bool WaitOver { get; set; }
    }
}
