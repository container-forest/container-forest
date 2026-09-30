using System.Collections.Concurrent;

namespace ContainerForest.Agent.Stacks;

/// <summary>
/// Lets one operation at a time run on a stack. A second one is refused rather than queued: the aggregator
/// reports it as busy, and a deploy or down that runs minutes after it was asked for would surprise the user.
/// </summary>
public sealed class StackLocks
{
    private readonly ConcurrentDictionary<string, byte> held = new(StringComparer.Ordinal);

    /// <returns>A lease that releases the stack when disposed, or null when another operation holds it.</returns>
    public IDisposable? TryAcquire(string stackName) =>
        held.TryAdd(stackName, 0) ? new Lease(this, stackName) : null;

    public bool IsHeld(string stackName) => held.ContainsKey(stackName);

    private sealed class Lease(StackLocks owner, string stackName) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.held.TryRemove(stackName, out _);
            }
        }
    }
}
