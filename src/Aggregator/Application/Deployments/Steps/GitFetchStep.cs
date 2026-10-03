using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Repositories;

namespace ContainerForest.Aggregator.Application.Deployments.Steps;

/// <summary>Fetches the stack's branch and reads the directory of its compose file; pins the deployment to the commit.</summary>
public sealed class GitFetchStep(IGitSource git, ISecretProtector protector, DeploymentOptions options) : IDeployStep
{
    public string Name => "Fetch from git";

    public async Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
    {
        var stack = context.Stack;
        var directory = stack.ComposePath.Directory;
        var commit = await git.FetchAsync(context.Repository, Credentials(context.Repository), stack.Branch, cancellationToken);

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

    private GitCredentials? Credentials(GitRepository repository)
    {
        if (repository.ProtectedAccessToken is null)
        {
            return null;
        }

        try
        {
            return new GitCredentials(
                repository.AccessUserName ?? GitRepository.DefaultAccessUserName,
                protector.Unprotect(SecretPurpose.GitAccessToken, repository.ProtectedAccessToken));
        }
        catch (SecretUnreadableException)
        {
            throw new DeploymentFailedException(
                $"The stored access token of repository '{repository.Name}' cannot be decrypted; set it again.");
        }
    }
}
