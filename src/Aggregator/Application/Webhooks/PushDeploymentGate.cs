using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;

namespace ContainerForest.Aggregator.Application.Webhooks;

/// <summary>
/// Remembers pushes and deploy hook calls that arrived while a stack was being deployed, so they are deployed after
/// that deployment instead of being lost: the running deployment has already fetched its commit and pulled its
/// images. Singleton.
/// </summary>
/// <remarks>
/// Deciding "running, so remember the push" and "finished, so deploy what was remembered" happen under one lock,
/// with the deployment's outcome already saved before the second: a push either sees the deployment unfinished and
/// is picked up after it, or sees it finished and queues its own. Kept in memory: a restart fails the running
/// deployment anyway (<see cref="Deployments.DeploymentRunner.FailInterruptedAsync"/>), and the push is then
/// redelivered from the git server.
/// </remarks>
public sealed class PushDeploymentGate
{
    private readonly SemaphoreSlim mutex = new(1, 1);
    private readonly Dictionary<StackId, ImagePullPolicy> pending = [];

    /// <summary>Holds the gate until the returned value is disposed.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await mutex.WaitAsync(cancellationToken);
        return new Releaser(mutex);
    }

    /// <summary>
    /// Call while holding the gate. <paramref name="pull"/> is what the follow-up deployment must pull; a deploy
    /// hook's <see cref="ImagePullPolicy.Always"/> is kept when a push comes after it.
    /// </summary>
    public void MarkPending(StackId stackId, ImagePullPolicy pull)
    {
        if (!pending.TryGetValue(stackId, out var marked) || marked != ImagePullPolicy.Always)
        {
            pending[stackId] = pull;
        }
    }

    /// <summary>
    /// Call while holding the gate. What the follow-up deployment must pull when something was waiting for the
    /// stack, which is then forgotten; null when nothing was.
    /// </summary>
    public ImagePullPolicy? TakePending(StackId stackId) => pending.Remove(stackId, out var pull) ? pull : null;

    private sealed class Releaser(SemaphoreSlim mutex) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                mutex.Release();
            }
        }
    }
}
