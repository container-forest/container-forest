using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Domain.Access;
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
    /// <summary>
    /// Fetches the branch and returns its head commit. Reading is a separate call, so the deployment is pinned to the
    /// commit even when its files turn out to be unusable.
    /// </summary>
    /// <param name="access">How to reach the repository: the connection's decrypted token, and whether its host is trusted.</param>
    /// <exception cref="DeploymentFailedException">The repository or the branch is unavailable.</exception>
    Task<CommitSha> FetchAsync(
        GitRepository repository, GitAccess access, BranchName branch, CancellationToken cancellationToken);

    /// <summary>Reads the files under <paramref name="directory"/> at a commit <see cref="FetchAsync"/> returned.</summary>
    /// <param name="directory">Directory inside the repository, '/'-separated; empty for the root.</param>
    /// <param name="maxBytes">Stops reading and fails when the files are larger in total.</param>
    /// <exception cref="DeploymentFailedException">The directory does not exist at the commit, or the files are too large.</exception>
    Task<GitSnapshot> ReadAsync(
        GitRepository repository, CommitSha commit, string directory, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the cached clone of a repository removed from the system. Best effort: a clone that cannot be deleted
    /// is only logged, since the repository is already gone.
    /// </summary>
    Task ForgetAsync(RepositoryId repositoryId, CancellationToken cancellationToken);
}

/// <summary>A decrypted access token for HTTPS basic authentication.</summary>
/// <param name="Scope">The connection's URL prefix: the token is sent only to URLs under it.</param>
public sealed record GitCredentials(string UserName, string Token, Uri Scope)
{
    // Records print every property; the token must not reach a log through ToString.
    public override string ToString() => $"{nameof(GitCredentials)} {{ UserName = {UserName}, Scope = {Scope} }}";
}

/// <summary>How the aggregator reaches a repository (ADR-0006).</summary>
/// <param name="Credentials">Null for anonymous access.</param>
/// <param name="IsTrustedHost">
/// The repository belongs to a connection an administrator set up, so its host may be on a private network. Otherwise
/// anyone with the Editor role could have added it, and private addresses are refused unless configured otherwise.
/// </param>
public sealed record GitAccess(GitCredentials? Credentials, bool IsTrustedHost)
{
    public static readonly GitAccess Public = new(null, IsTrustedHost: false);
}

/// <param name="Files">Paths are relative to the requested directory.</param>
/// <param name="SkippedPaths">Entries that cannot be deployed as files: symbolic links, submodules, unsupported names.</param>
/// <param name="OuterAttributes">
/// The .gitattributes files of the directories above the one read (from the root down); those inside it are among
/// <paramref name="Files"/>. They say which files the forest git filter encrypts (ADR-0010).
/// </param>
public sealed record GitSnapshot(
    CommitSha Commit,
    IReadOnlyList<SourceFile> Files,
    IReadOnlyList<string> SkippedPaths,
    IReadOnlyList<GitAttributesFile>? OuterAttributes = null);

/// <param name="IsSecret">Decrypted from SOPS: the agent keeps it in memory (tmpfs), never on the host's disk.</param>
public sealed record SourceFile(StackFilePath Path, byte[] Content, bool IsExecutable, bool IsSecret = false);

/// <summary>Reads the parts of a compose file the pipeline checks. Implemented with YamlDotNet.</summary>
public interface IComposeParser
{
    /// <exception cref="DeploymentFailedException">The file is not valid YAML or has no services.</exception>
    ComposeFile Parse(string yaml);
}

/// <param name="Includes">Paths of the top-level "include" section as written: compose files and their env files.</param>
/// <param name="HasTags">Some node has an explicit YAML tag, such as !reset or !override, which changes how compose merges.</param>
public sealed record ComposeFile(IReadOnlyList<ComposeService> Services, IReadOnlyList<string>? Includes = null, bool HasTags = false)
{
    public IReadOnlyList<string> IncludedFiles => Includes ?? [];
}

/// <param name="Image">Value of "image" as written, before variable interpolation; null when absent.</param>
/// <param name="HasBuild">The service has a "build" section.</param>
/// <param name="Extends">The service extends another one, which may supply the image.</param>
/// <param name="FileReferences">Files compose reads for the service as written: env_file, label_file, extends.file.</param>
public sealed record ComposeService(string Name, string? Image, bool HasBuild, bool Extends, IReadOnlyList<string>? FileReferences = null)
{
    public IReadOnlyList<string> ReferencedFiles => FileReferences ?? [];
}

/// <summary>Hands queued deployments to the background worker.</summary>
public interface IDeploymentQueue
{
    void Enqueue(DeploymentId id);
}

public interface IDeploymentRepository : IRepository<Deployment, DeploymentId>
{
    /// <summary>True when the stack has a queued or running deployment.</summary>
    Task<bool> HasUnfinishedAsync(StackId stackId, CancellationToken cancellationToken);

    /// <summary>The stack's queued or running deployment; there is at most one.</summary>
    Task<Deployment?> FindUnfinishedAsync(StackId stackId, CancellationToken cancellationToken);

    /// <summary>Queued and running deployments of all stacks, e.g. those interrupted by a restart.</summary>
    Task<IReadOnlyList<Deployment>> ListUnfinishedAsync(CancellationToken cancellationToken);

    /// <summary>True when a deployment of the stack ran the commit as approved unsigned by its SHA (ADR-0011).</summary>
    Task<bool> WasApprovedAsync(StackId stackId, CommitSha commit, CancellationToken cancellationToken);

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
    string? Error,
    CommitEndorsement Endorsement = CommitEndorsement.NotChecked,
    string? SignerName = null);

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

    /// <summary>How long the agent may take to render a stack for the compose policy (ADR-0012); nothing is pulled.</summary>
    public TimeSpan RenderTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public bool IsValid() =>
        MaxBundleBytes > 0 && AgentTimeout > TimeSpan.Zero && MaxConcurrentDeployments > 0 && RenderTimeout > TimeSpan.Zero;
}

/// <summary>The signature git stored in a commit and the bytes it covers.</summary>
/// <param name="Signature">The "gpgsig" header as git wrote it: armored SSH, GPG or X.509.</param>
/// <param name="SignedData">The commit object without its signature, which is what was signed.</param>
public sealed record CommitSignatureData(string Signature, byte[] SignedData);

/// <summary>Reads what the commit graph says about fetched commits (ADR-0011). Implemented with LibGit2Sharp.</summary>
public interface ICommitInspector
{
    /// <summary>The commit's signature; null for an unsigned commit.</summary>
    /// <exception cref="DeploymentFailedException">The commit has not been fetched.</exception>
    Task<CommitSignatureData?> ReadSignatureAsync(GitRepository repository, CommitSha commit, CancellationToken cancellationToken);

    /// <summary>
    /// True when <paramref name="commit"/> is <paramref name="ancestor"/> or descends from it. False when the
    /// ancestor is not in the clone, e.g. after the stack was pointed at another repository.
    /// </summary>
    Task<bool> DescendsFromAsync(GitRepository repository, CommitSha commit, CommitSha ancestor, CancellationToken cancellationToken);

    /// <summary>The paths a commit changes against its only parent, and one file at both (ADR-0015).</summary>
    /// <param name="path">The file to read, '/'-separated from the repository root.</param>
    /// <returns>Null when the commit is not in the clone or does not have exactly one parent.</returns>
    Task<CommitDiff?> DiffAsync(GitRepository repository, CommitSha commit, string path, CancellationToken cancellationToken);
}

/// <param name="Parent">The commit's only parent.</param>
/// <param name="ChangedPaths">Every path that differs from the parent, files only, '/'-separated.</param>
/// <param name="Before">The file at the parent; null when absent.</param>
/// <param name="After">The file at the commit; null when absent.</param>
/// <param name="ModeChanged">The file's mode differs between the two.</param>
public sealed record CommitDiff(CommitSha Parent, IReadOnlyList<string> ChangedPaths, byte[]? Before, byte[]? After, bool ModeChanged = false);

/// <summary>Checks an SSH signature cryptographically. Implemented with <c>ssh-keygen -Y verify</c>.</summary>
public interface ICommitSignatureVerifier
{
    /// <summary>True when <paramref name="signature"/> is a valid git signature of <paramref name="signedData"/> by <paramref name="key"/>.</summary>
    /// <exception cref="DeploymentFailedException">ssh-keygen is missing or does not answer.</exception>
    Task<bool> VerifyAsync(SshPublicKey key, SshSignature signature, byte[] signedData, CancellationToken cancellationToken);
}
