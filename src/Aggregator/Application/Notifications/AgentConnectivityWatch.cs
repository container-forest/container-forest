using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Notifications;

/// <summary>
/// Decides when an agent's connection changes are worth a notification. A disconnect is notified only after
/// <see cref="NotificationOptions.AgentOfflineDelay"/> without a new connection, and a connection only when it
/// ends such an outage: an agent that reconnects within the delay produces nothing.
/// </summary>
/// <remarks>
/// The state lives in memory. An aggregator restart forgets pending and notified outages: the shutdown itself
/// cancels the pending ones (it is not the agents' outage), and an agent that does not come back after the
/// restart is not notified about.
/// </remarks>
public sealed class AgentConnectivityWatch(
    IServiceScopeFactory scopeFactory,
    NotificationOptions options,
    TimeProvider time,
    ILogger<AgentConnectivityWatch> logger) : IDisposable
{
    private readonly Lock gate = new();
    private readonly Dictionary<EnvironmentId, Outage> outages = [];
    private readonly Dictionary<EnvironmentId, Task> deliveries = [];
    private readonly CancellationTokenSource stopping = new();
    private bool disposed;

    /// <summary>The agent of the environment connected (its session is open).</summary>
    public void Connected(EnvironmentId environmentId, DateTimeOffset connectedAt)
    {
        lock (gate)
        {
            if (disposed || !outages.Remove(environmentId, out var outage))
            {
                return;
            }

            if (!outage.DelayOver)
            {
                outage.Waiting.Cancel();
            }

            if (outage.Notified)
            {
                Enqueue(environmentId, NotificationEventKinds.AgentOnline, connectedAt);
            }
        }
    }

    /// <summary>The current session of the environment's agent ended.</summary>
    /// <param name="lastSeenAt">The agent's last message: when the outage started, as far as the aggregator knows.</param>
    public void Disconnected(EnvironmentId environmentId, DateTimeOffset lastSeenAt)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (outages.ContainsKey(environmentId))
            {
                // Already waiting or notified: the outage started earlier, and it is still the same one.
                return;
            }

            var outage = new Outage(CancellationTokenSource.CreateLinkedTokenSource(stopping.Token));
            outages[environmentId] = outage;
            _ = NotifyWhenStillOfflineAsync(environmentId, outage, lastSeenAt);
        }
    }

    /// <summary>Cancels the outages being waited out: a shutting-down aggregator is not the agents' outage.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            stopping.Cancel();
        }
    }

    private async Task NotifyWhenStillOfflineAsync(EnvironmentId environmentId, Outage outage, DateTimeOffset lastSeenAt)
    {
        try
        {
            await Task.Delay(options.AgentOfflineDelay, time, outage.Waiting.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            lock (gate)
            {
                outage.DelayOver = true;
                outage.Waiting.Dispose();
            }
        }

        lock (gate)
        {
            // A connection during the delay ended this outage, even if the cancellation came too late for the delay.
            if (disposed || !outages.TryGetValue(environmentId, out var current) || current != outage)
            {
                return;
            }

            outage.Notified = true;
            Enqueue(environmentId, NotificationEventKinds.AgentOffline, lastSeenAt);
        }
    }

    /// <summary>Runs after the environment's previous notification, so "back online" never overtakes "offline".</summary>
    private void Enqueue(EnvironmentId environmentId, NotificationEventKinds kind, DateTimeOffset occurredAt)
    {
        var previous = deliveries.GetValueOrDefault(environmentId, Task.CompletedTask);
        deliveries[environmentId] = previous
            .ContinueWith(_ => NotifyAsync(environmentId, kind, occurredAt), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
            .Unwrap();
    }

    private async Task NotifyAsync(EnvironmentId environmentId, NotificationEventKinds kind, DateTimeOffset occurredAt)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AgentConnectivityNotifier>()
                .NotifyAsync(environmentId, kind, occurredAt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The connection itself is fine; a lost notification is only logged.
            logger.LogError(ex, "Cannot create {Kind} notifications for environment {EnvironmentId}", kind, environmentId);
        }
    }

    private sealed class Outage(CancellationTokenSource waiting)
    {
        /// <summary>Cancelled when the agent connects again before the delay is over.</summary>
        public CancellationTokenSource Waiting { get; } = waiting;

        /// <summary>Set once <see cref="Waiting"/> is disposed; there is nothing left to cancel.</summary>
        public bool DelayOver { get; set; }

        /// <summary>"Agent offline" was sent; the next connection sends "back online".</summary>
        public bool Notified { get; set; }
    }
}
