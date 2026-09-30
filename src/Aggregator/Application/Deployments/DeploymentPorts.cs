using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Deployments;

/// <summary>A deployment step failed for a reason the user can act on. The message is shown in the panel.</summary>
public sealed class DeploymentFailedException(string message) : Exception(message);

/// <summary>Reads stack sources from git. Implemented with LibGit2Sharp in the infrastructure layer.</summary>
public interface IGitSource
{
    /// <summary>Fetches the branch and reads the files under <paramref name="directory"/> at its head commit.</summary>
    /// <param name="directory">Directory inside the repository, '/'-separated; empty for the root.</param>
    /// <param name="maxBytes">Stops reading and fails when the files are larger in total.</param>
    /// <exception cref="DeploymentFailedException">The repository, branch or directory is unavailable, or the files are too large.</exception>
    Task<GitSnapshot> FetchAsync(
        GitRepository repository, BranchName branch, string directory, long maxBytes, CancellationToken cancellationToken);
}

/// <param name="Files">Paths are relative to the requested directory.</param>
/// <param name="SkippedPaths">Entries that cannot be deployed as files: symbolic links, submodules, unsupported names.</param>
public sealed record GitSnapshot(CommitSha Commit, IReadOnlyList<SourceFile> Files, IReadOnlyList<string> SkippedPaths);

public sealed record SourceFile(StackFilePath Path, byte[] Content, bool IsExecutable);

/// <summary>Reads the parts of a compose file the pipeline checks. Implemented with YamlDotNet.</summary>
public interface IComposeParser
{
    /// <exception cref="DeploymentFailedException">The file is not valid YAML or has no services.</exception>
    ComposeFile Parse(string yaml);
}

public sealed record ComposeFile(IReadOnlyList<ComposeService> Services);

/// <param name="Image">Value of "image" as written, before variable interpolation; null when absent.</param>
/// <param name="HasBuild">The service has a "build" section.</param>
/// <param name="Extends">The service extends another one, which may supply the image.</param>
public sealed record ComposeService(string Name, string? Image, bool HasBuild, bool Extends);

/// <summary>Hands queued deployments to the background worker.</summary>
public interface IDeploymentQueue
{
    void Enqueue(DeploymentId id);
}

public interface IDeploymentRepository : IRepository<Deployment, DeploymentId>
{
    /// <summary>True when the stack has a queued or running deployment.</summary>
    Task<bool> HasUnfinishedAsync(StackId stackId, CancellationToken cancellationToken);

    /// <summary>Queued and running deployments of all stacks, e.g. those interrupted by a restart.</summary>
    Task<IReadOnlyList<Deployment>> ListUnfinishedAsync(CancellationToken cancellationToken);

    /// <summary>Newest first, without logs.</summary>
    Task<IReadOnlyList<DeploymentSummary>> ListByStackAsync(StackId stackId, int limit, CancellationToken cancellationToken);
}

public sealed record DeploymentSummary(
    DeploymentId Id,
    DeploymentStatus Status,
    DeploymentTrigger Trigger,
    ImagePullPolicy PullPolicy,
    CommitSha? Commit,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error);

/// <summary>Configuration section "Deployments" (env: CF_DEPLOYMENTS__...).</summary>
public sealed class DeploymentOptions
{
    public const string SectionName = "Deployments";

    /// <summary>
    /// Largest bundle the aggregator sends to an agent. gRPC limits a message to 4 MB by default,
    /// and the bundle travels in one message together with its framing.
    /// </summary>
    public long MaxBundleBytes { get; set; } = 3 * 1024 * 1024;

    /// <summary>How long the aggregator waits for the agent's result; compose pulls images within this time.</summary>
    public TimeSpan AgentTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Deployments of different stacks that may run at the same time.</summary>
    public int MaxConcurrentDeployments { get; set; } = 4;

    public bool IsValid() => MaxBundleBytes > 0 && AgentTimeout > TimeSpan.Zero && MaxConcurrentDeployments > 0;
}
