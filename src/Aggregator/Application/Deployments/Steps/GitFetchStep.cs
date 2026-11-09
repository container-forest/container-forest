using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Deployments.Steps;

/// <summary>
/// Fetches the stack's branch and reads the directory of its compose file; pins the deployment to the commit.
/// A deployment pinned from the start (rehydration) reads its own commit instead of the branch head.
/// </summary>
public sealed class GitFetchStep(IGitSource git, ISecretProtector protector, DeploymentOptions options) : IDeployStep
{
    public string Name => "Fetch from git";

    public async Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
    {
        var stack = context.Stack;
        EnsureUsable(context);

        var directory = stack.ComposePath.Directory;
        var commit = context.Deployment is { IsPinned: true, Commit: { } pinned }
            ? await FetchPinnedAsync(context, pinned, cancellationToken)
            : await git.FetchAsync(context.Repository, Access(context), stack.Branch, cancellationToken);

        // Before reading: a deployment that fails on the files still shows which commit it was.
        context.Deployment.ResolveCommit(commit);
        var source = await git.ReadAsync(context.Repository, commit, directory, options.MaxBundleBytes, cancellationToken);
        context.Source = source;

        var where = directory.Length == 0 ? "repository root" : $"'{directory}'";
        context.Info($"Commit {source.Commit.Short} on {stack.Branch}: {source.Files.Count} file(s) in {where}.");
        foreach (var skipped in source.SkippedPaths)
        {
            context.Warn($"Skipped '{skipped}': only regular files are deployed (no symbolic links or submodules).");
        }
    }

    /// <summary>
    /// The pinned commit is usually in the cache already, since it was deployed from it. The branch is fetched anyway,
    /// in case the cache was lost; when the git server is unreachable, as it may be right after a power cut, the
    /// cached commit is enough.
    /// </summary>
    private async Task<CommitSha> FetchPinnedAsync(DeployContext context, CommitSha pinned, CancellationToken cancellationToken)
    {
        try
        {
            await git.FetchAsync(context.Repository, Access(context), context.Stack.Branch, cancellationToken);
        }
        catch (DeploymentFailedException ex)
        {
            context.Warn($"{ex.Message} Using commit {pinned.Short} from the aggregator's cache.");
        }

        context.Info($"Deploying the pinned commit {pinned.Short} again, not the head of {context.Stack.Branch}.");
        return pinned;
    }

    /// <summary>
    /// The stack was added while the environment could use the repository; an administrator may have taken the
    /// environment off the connection since (ADR-0006). The token is not sent for it, whoever started the deployment.
    /// </summary>
    private static void EnsureUsable(DeployContext context)
    {
        if (!context.Repository.IsUsableIn(context.Environment.Id, context.Connection))
        {
            throw new DeploymentFailedException(
                $"Environment '{context.Environment.Name}' may not deploy from repository '{context.Repository.Name}': " +
                $"its connection '{context.Connection!.Name}' is not allowed here. Ask an administrator to allow it.");
        }
    }

    private GitAccess Access(DeployContext context) => GitAccessResolver.ForFetch(context.Connection, protector);
}
