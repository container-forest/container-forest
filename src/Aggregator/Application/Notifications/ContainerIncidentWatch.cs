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
/// <item>"oom" of the container within <see cref="NotificationOptions.ContainerStopWait"/> before or after its crash marks
/// it as killed for running out of memory. Docker reports it before "die"; an "oom" without a death (the kernel killed
/// another process of the container) is not notified.</item>
/// <item>"health_status: unhealthy" right away, and "health_status: healthy" of a container whose unhealthy notification
/// went out, once, whatever the repeat interval: it closes that notification.</item>
/// </list>
/// One container gets at most one notification of a kind per <see cref="NotificationOptions.ContainerRepeatInterval"/>.
/// </summary>
/// <remarks>
/// The state lives in memory: deaths being waited out when the aggregator stops are not notified, nor is the recovery
/// of a container that turned unhealthy before it.
/// </remarks>
public sealed class ContainerIncidentWatch(
    IServiceScopeFactory scopeFactory,
    NotificationOptions options,
    TimeProvider time,
    ILogger<ContainerIncidentWatch> logger) : IDisposable
{
    public const string DieAction = "die";
    public const string StopAction = "stop";
    public const string DestroyAction = "destroy";
    public const string OutOfMemoryAction = "oom";
    public const string UnhealthyAction = "health_status: unhealthy";
    public const string HealthyAction = "health_status: healthy";

    private readonly Lock gate = new();
    private readonly Dictionary<(EnvironmentId, string), PendingDeath> deaths = [];
    private readonly Dictionary<(EnvironmentId, string), DateTimeOffset> requestedStops = [];
    private readonly Dictionary<(EnvironmentId, string), DateTimeOffset> outOfMemory = [];
    private readonly Dictionary<(EnvironmentId, string, NotificationEventKinds), DateTimeOffset> lastNotified = [];

    // By name, like lastNotified: containers whose unhealthy notification went out and that have not recovered since.
    private readonly HashSet<(EnvironmentId, string)> unhealthy = [];
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
                case OutOfMemoryAction:
                    RanOutOfMemory(container);
                    break;
                case UnhealthyAction:
                    if (Raise(observation, NotificationEventKinds.ContainerUnhealthy))
                    {
                        unhealthy.Add((observation.EnvironmentId, observation.DisplayName));
                    }

                    break;
                case HealthyAction:
                    // Not throttled: it pairs with an unhealthy notification that was sent, and must not be lost after it.
                    if (unhealthy.Remove((observation.EnvironmentId, observation.DisplayName)))
                    {
                        _ = NotifyAsync(observation, NotificationEventKinds.ContainerHealthy);
                    }

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
            var killed = outOfMemory.Remove(container, out var oomAt) && time.GetUtcNow() - oomAt < options.ContainerStopWait;
            var death = new PendingDeath(
                observation with { OutOfMemory = killed }, CancellationTokenSource.CreateLinkedTokenSource(stopping.Token));
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

        Remember(requestedStops, container);
    }

    /// <summary>An "oom": the death being waited out, or the one about to come, was the kernel's doing. Called under the lock.</summary>
    private void RanOutOfMemory((EnvironmentId, string) container)
    {
        if (deaths.TryGetValue(container, out var death))
        {
            death.Observation = death.Observation with { OutOfMemory = true };
            return;
        }

        Remember(outOfMemory, container);
    }

    /// <summary>Records an event for the death to come, forgetting the ones older than the wait. Called under the lock.</summary>
    private void Remember(Dictionary<(EnvironmentId, string), DateTimeOffset> events, (EnvironmentId, string) container)
    {
        var now = time.GetUtcNow();
        foreach (var stale in events.Where(e => now - e.Value >= options.ContainerStopWait).Select(e => e.Key).ToList())
        {
            events.Remove(stale);
        }

        events[container] = now;
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
            if (death.Observation.ExitCode != 0 || death.Observation.OutOfMemory)
            {
                Raise(death.Observation, NotificationEventKinds.ContainerDied);
            }
        }
    }

    /// <summary>Sends the notification unless one of this kind about this container went out recently. Called under the lock.</summary>
    /// <returns>Whether it was sent.</returns>
    private bool Raise(ContainerObservation observation, NotificationEventKinds kind)
    {
        var now = time.GetUtcNow();

        // A recreated container keeps its name, not its id: the name is what a person recognizes as "the same".
        var key = (observation.EnvironmentId, observation.DisplayName, kind);
        if (lastNotified.TryGetValue(key, out var last) && now - last < options.ContainerRepeatInterval)
        {
            return false;
        }

        foreach (var stale in lastNotified.Where(e => now - e.Value >= options.ContainerRepeatInterval).Select(e => e.Key).ToList())
        {
            lastNotified.Remove(stale);
        }

        lastNotified[key] = now;
        _ = NotifyAsync(observation, kind);
        return true;
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
        /// <summary>The "die"; an "oom" that comes after it marks it as <see cref="ContainerObservation.OutOfMemory"/>.</summary>
        public ContainerObservation Observation { get; set; } = observation;

        /// <summary>Cancelled by the container's "stop".</summary>
        public CancellationTokenSource Waiting { get; } = waiting;

        /// <summary>Set once <see cref="Waiting"/> is disposed; there is nothing left to cancel.</summary>
        public bool WaitOver { get; set; }
    }
}
