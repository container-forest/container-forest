using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Images;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Images;

public interface IImagePolicyRepository : IRepository<ImagePolicy, ImagePolicyId>
{
    /// <summary>In image order.</summary>
    Task<IReadOnlyList<ImagePolicy>> ListByStackAsync(StackId stackId, CancellationToken cancellationToken);

    /// <summary>Policies the poller checks: <see cref="ImagePolicy.Poll"/> and not pinned.</summary>
    Task<IReadOnlyList<ImagePolicy>> ListPolledAsync(CancellationToken cancellationToken);
}

public interface IImageUpdateRecordRepository : IRepository<ImageUpdateRecord, ImageUpdateRecordId>
{
    Task<ImageUpdateRecord?> FindByCommitAsync(StackId stackId, CommitSha commit, CancellationToken cancellationToken);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<ImageUpdateRecord>> ListByStackAsync(StackId stackId, int limit, CancellationToken cancellationToken);
}

/// <summary>What the aggregator learned about tags of a repository (<see cref="ImageTagCacheEntry"/>).</summary>
public interface IImageTagCache
{
    /// <summary>The known tags of the image, by tag.</summary>
    Task<IReadOnlyDictionary<string, ImageTagCacheEntry>> ListAsync(ImageName image, CancellationToken cancellationToken);

    /// <summary>Adds or replaces an entry; saved with the unit of work.</summary>
    Task PutAsync(ImageTagCacheEntry entry, CancellationToken cancellationToken);
}

public interface IAggregatorSigningKeyRepository : IRepository<AggregatorSigningKey, AggregatorSigningKeyId>
{
    Task<AggregatorSigningKey?> FindActiveAsync(CancellationToken cancellationToken);

    /// <summary>Newest first, retired ones included.</summary>
    Task<IReadOnlyList<AggregatorSigningKey>> ListAsync(CancellationToken cancellationToken);

    /// <summary>True when the fingerprint is of any key the aggregator has had, active or retired.</summary>
    Task<bool> ExistsAsync(string fingerprint, CancellationToken cancellationToken);
}

/// <summary>A login for a registry, decrypted.</summary>
public sealed record RegistryAuth(string Username, string Password)
{
    // Records print every property; the password must not reach a log through ToString.
    public override string ToString() => $"{nameof(RegistryAuth)} {{ Username = {Username} }}";
}

/// <summary>What a registry says about one tag.</summary>
/// <param name="Digest">"sha256:…" of the manifest or index the tag points to.</param>
/// <param name="Created">The "created" time of the image's configuration; null when not asked for or not set.</param>
public sealed record RegistryImage(string Digest, DateTimeOffset? Created);

/// <summary>The registry could not be read: unreachable, an error status, an answer that is not a registry's.</summary>
/// <param name="rateLimited">It answered 429: the policy's order costs more requests than it allows.</param>
public sealed class RegistryUnavailableException(string message, bool rateLimited = false) : Exception(message)
{
    public bool RateLimited { get; } = rateLimited;
}

/// <summary>The registry says there is no such repository or tag.</summary>
public sealed class RegistryNotFoundException(string message) : Exception(message);

/// <summary>Reads OCI distribution registries (ADR-0015, item 9). Implemented on HttpClient in the infrastructure layer.</summary>
public interface IRegistryClient
{
    /// <summary>Every tag of the repository, following the registry's pages.</summary>
    /// <exception cref="RegistryUnavailableException">The registry could not be read.</exception>
    /// <exception cref="RegistryNotFoundException">No such repository, or no access to it.</exception>
    Task<IReadOnlyList<string>> ListTagsAsync(ImageName image, RegistryAuth? auth, CancellationToken cancellationToken);

    /// <summary>The digest of a tag and, with <paramref name="readCreated"/>, when its image was built.</summary>
    /// <exception cref="RegistryUnavailableException">The registry could not be read.</exception>
    /// <exception cref="RegistryNotFoundException">No such tag.</exception>
    Task<RegistryImage> GetImageAsync(
        ImageName image, string tag, RegistryAuth? auth, bool readCreated, CancellationToken cancellationToken);
}

/// <summary>Changes "image" values of a compose file in place (ADR-0015, item 4). Implemented with YamlDotNet.</summary>
public interface IComposeImageRewriter
{
    /// <summary>The services the policy updates, with their image values as written.</summary>
    /// <exception cref="ImagePolicyException">The file cannot be read, or a matching service cannot be rewritten.</exception>
    IReadOnlyList<ComposeImageSlot> Find(string yaml, ImagePolicy policy);

    /// <summary>
    /// Writes <paramref name="value"/> into every slot <see cref="Find"/> returns, leaving every other byte as it was.
    /// </summary>
    /// <param name="value">The new value without a name, e.g. "main-1234567" or "main-1234567@sha256:…".</param>
    /// <exception cref="ImagePolicyException">The file cannot be rewritten.</exception>
    ComposeRewrite Rewrite(string yaml, ImagePolicy policy, string value);

    /// <summary>
    /// The file with exactly these changes applied, for checking an update commit (ADR-0015, item 6); null when a change
    /// does not apply, e.g. its service's value is not its <see cref="ImageChange.From"/>.
    /// </summary>
    string? Apply(string yaml, IReadOnlyList<ImageChange> changes);
}

/// <param name="Value">The image as written, e.g. "gitea.rup.rocks/app:main-1234567".</param>
/// <param name="Tag">Its tag; null without one.</param>
/// <param name="Digest">Its digest; null without one.</param>
public sealed record ComposeImageSlot(string Service, string Value, string? Tag, string? Digest);

public sealed record ComposeRewrite(string Yaml, IReadOnlyList<ImageChange> Changes);

/// <summary>A policy cannot do its work for a reason its Editor has to fix; the message says what.</summary>
public sealed class ImagePolicyException(string message) : Exception(message);

/// <summary>Signs git commits with the aggregator's key (ADR-0015, item 6). Implemented with <c>ssh-keygen -Y sign</c>.</summary>
public interface ICommitSigner
{
    /// <summary>The armored SSH signature of <paramref name="data"/> in the git namespace, for a commit's "gpgsig" header.</summary>
    Task<string> SignAsync(string privateKey, byte[] data, CancellationToken cancellationToken);
}

/// <summary>A new SSH key pair. Implemented with <c>ssh-keygen -t ed25519</c>.</summary>
public interface ISshKeyGenerator
{
    Task<GeneratedSshKey> GenerateAsync(string comment, CancellationToken cancellationToken);
}

/// <param name="PrivateKey">OpenSSH private key; to be encrypted at once.</param>
/// <param name="PublicKey">"ssh-ed25519 AAAA… comment".</param>
public sealed record GeneratedSshKey(string PrivateKey, string PublicKey)
{
    public override string ToString() => $"{nameof(GeneratedSshKey)} {{ PublicKey = {PublicKey} }}";
}

/// <summary>A commit for the git writer: one file replaced on top of a parent.</summary>
/// <param name="Path">The file in the repository, '/'-separated.</param>
public sealed record GitFileCommit(
    CommitSha Parent,
    string Path,
    byte[] Content,
    string Message,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset When);

/// <summary>The git server refused the push because the branch moved: fetch, check and try again.</summary>
public sealed class GitPushRejectedException(string message) : Exception(message);

/// <summary>Writes to stack repositories (ADR-0015, item 5). Implemented with LibGit2Sharp, in the same bare clone as fetches.</summary>
public interface IGitWriter
{
    /// <summary>Creates the commit in the clone, signed with <paramref name="sign"/>, without pushing it.</summary>
    /// <param name="sign">Signs the commit object; returns the armored signature.</param>
    /// <exception cref="DeploymentFailedException">The parent has not been fetched or the file cannot be written.</exception>
    Task<CommitSha> CreateCommitAsync(
        GitRepository repository, GitFileCommit commit, Func<byte[], Task<string>> sign, CancellationToken cancellationToken);

    /// <summary>Pushes the commit to the branch, fast-forward only, and moves the clone's tracking branch to it.</summary>
    /// <exception cref="GitPushRejectedException">The branch has moved on.</exception>
    /// <exception cref="DeploymentFailedException">The push failed for another reason.</exception>
    Task PushAsync(GitRepository repository, GitAccess access, CommitSha commit, BranchName branch, CancellationToken cancellationToken);
}

