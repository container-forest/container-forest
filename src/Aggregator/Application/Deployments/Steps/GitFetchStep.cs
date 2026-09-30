namespace ContainerForest.Aggregator.Application.Deployments.Steps;

/// <summary>Fetches the stack's branch and reads the directory of its compose file; pins the deployment to the commit.</summary>
public sealed class GitFetchStep(IGitSource git, DeploymentOptions options) : IDeployStep
{
    public string Name => "Fetch from git";

    public async Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
    {
        var stack = context.Stack;
        var directory = stack.ComposePath.Directory;
        var source = await git.FetchAsync(context.Repository, stack.Branch, directory, options.MaxBundleBytes, cancellationToken);

        context.Deployment.ResolveCommit(source.Commit);
        context.Source = source;

        var where = directory.Length == 0 ? "repository root" : $"'{directory}'";
        context.Info($"Commit {source.Commit.Short} on {stack.Branch}: {source.Files.Count} file(s) in {where}.");
        foreach (var skipped in source.SkippedPaths)
        {
            context.Warn($"Skipped '{skipped}': only regular files are deployed (no symbolic links or submodules).");
        }
    }
}
