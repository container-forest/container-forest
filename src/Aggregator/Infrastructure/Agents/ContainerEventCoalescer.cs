using System.Collections.Concurrent;
using ContainerForest.Aggregator.Application.Live;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Infrastructure.Agents;

/// <summary>
/// Turns the container events of an agent into live updates, at most one per environment and <see cref="Window"/>.
/// A compose up reports create, start and health for every container; panels need to reload the list once,
/// not twenty times, and each reload asks the agent for all its containers.
/// </summary>
public sealed class ContainerEventCoalescer(ILiveUpdates live, TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<EnvironmentId, byte> pending = new();

    /// <summary>The first event of a window schedules the update; later ones in the same window join it.</summary>
    public void Report(EnvironmentId environmentId)
    {
        if (pending.TryAdd(environmentId, 0))
        {
            _ = PublishAfterWindowAsync(environmentId);
        }
    }

    private async Task PublishAfterWindowAsync(EnvironmentId environmentId)
    {
        await Task.Delay(Window, time);

        // Removed before publishing: an event that arrives while panels reload starts the next window.
        pending.TryRemove(environmentId, out _);
        await live.ContainersChangedAsync(environmentId);
    }
}
