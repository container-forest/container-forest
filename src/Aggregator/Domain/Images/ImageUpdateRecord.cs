using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Domain.Images;

/// <summary>Why the aggregator made an update commit.</summary>
public enum ImageUpdateReason
{
    /// <summary>A check found a newer tag: polling, or the image hook asking for a check.</summary>
    Policy,

    /// <summary>The image hook asked for an exact tag.</summary>
    Hook,

    /// <summary>A user asked for an exact tag, which pins it.</summary>
    User,
}

/// <summary>One "image" value the update changed.</summary>
/// <param name="From">The value as it was written, e.g. "app:main-1234567".</param>
/// <param name="To">The value written instead.</param>
public sealed record ImageChange(string Service, string From, string To);

/// <summary>
/// An update commit the aggregator created (ADR-0015). It is what endorses the commit, not the aggregator's signature:
/// the commit is endorsed when its parent is and it changes nothing but these values of the stack's compose file.
/// </summary>
public sealed class ImageUpdateRecord : Entity<ImageUpdateRecordId>
{
    public const int MaxNameLength = 128;

    private List<ImageChange> changes = [];

    private ImageUpdateRecord(
        ImageUpdateRecordId id,
        StackId stackId,
        ImagePolicyId policyId,
        CommitSha commit,
        CommitSha parent,
        StackFilePath composePath,
        string tag,
        string? digest,
        ImageUpdateReason reason,
        UserId? requestedBy,
        string? requestedByName,
        DateTimeOffset createdAt)
        : base(id)
    {
        StackId = stackId;
        PolicyId = policyId;
        Commit = commit;
        Parent = parent;
        ComposePath = composePath;
        Tag = tag;
        Digest = digest;
        Reason = reason;
        RequestedBy = requestedBy;
        RequestedByName = requestedByName;
        CreatedAt = createdAt;
    }

    // For EF Core.
    private ImageUpdateRecord()
    {
        Commit = null!;
        Parent = null!;
        ComposePath = null!;
        Tag = null!;
    }

    public StackId StackId { get; private init; }

    /// <summary>The policy it was made for; it may have been removed since, the record stays.</summary>
    public ImagePolicyId PolicyId { get; private init; }

    public CommitSha Commit { get; private init; }

    /// <summary>The commit's only parent: the branch head the update was made on.</summary>
    public CommitSha Parent { get; private init; }

    /// <summary>The one file the commit changes.</summary>
    public StackFilePath ComposePath { get; private init; }

    public string Tag { get; private init; }

    /// <summary>"sha256:…" of the image; set when the policy pins digests.</summary>
    public string? Digest { get; private init; }

    public IReadOnlyList<ImageChange> Changes => changes;

    public ImageUpdateReason Reason { get; private init; }

    /// <summary>The user who asked for the tag; null for the policy and the hook.</summary>
    public UserId? RequestedBy { get; private init; }

    /// <summary>Their name at the time.</summary>
    public string? RequestedByName { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    /// <exception cref="DomainValidationException">No changes: an update commit that changes nothing is a mistake.</exception>
    /// <param name="id">Known before the commit is made, since its message names the record.</param>
    public static ImageUpdateRecord Create(
        ImageUpdateRecordId id,
        StackId stackId,
        ImagePolicyId policyId,
        CommitSha commit,
        CommitSha parent,
        StackFilePath composePath,
        string tag,
        string? digest,
        IReadOnlyList<ImageChange> changes,
        ImageUpdateReason reason,
        UserId? requestedBy,
        string? requestedByName,
        DateTimeOffset now)
    {
        if (changes.Count == 0)
        {
            throw new DomainValidationException("An image update record needs at least one change.");
        }

        if (commit == parent)
        {
            throw new DomainValidationException("An update commit cannot be its own parent.");
        }

        var name = requestedByName is { Length: > MaxNameLength } ? requestedByName[..MaxNameLength] : requestedByName;
        return new ImageUpdateRecord(
            id, stackId, policyId, commit, parent, composePath, tag, digest, reason, requestedBy, name, now)
        {
            changes = [.. changes],
        };
    }
}
