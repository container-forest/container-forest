using ContainerForest.Aggregator.Application.Images;
using ContainerForest.Aggregator.Domain.Images;

namespace ContainerForest.Aggregator.Api.Contracts;

// Image updates by the aggregator (ADR-0015): policies on stacks, checks, pins, the stack's image hook and the
// aggregator's own signing key.
//
//   GET    /api/stacks/{id}/image-policies               -> ImagePolicyListResponse; anyone who sees the stack
//   POST   /api/stacks/{id}/image-policies               -> 201 ImagePolicyResponse; Editor on the stack; 400 when the
//                                                           compose file at the branch head has no service to update or
//                                                           the connection has no push token; 409 for a system stack,
//                                                           a second policy for the image or more than 10
//   PUT    /api/image-policies/{policyId}                -> ImagePolicyResponse; Editor on the stack
//   DELETE /api/image-policies/{policyId}                -> 204; Editor on the stack
//   POST   /api/image-policies/{policyId}/check          -> ImageUpdateResultResponse; ?dryRun=true for anyone who sees the
//                                                           stack (nothing changes), Editor otherwise; 503 when the agent is
//                                                           offline and there is something to commit
//   POST   /api/image-policies/{policyId}/pin            -> ImageUpdateResultResponse; Editor; updates to the tag and pins it
//   POST   /api/image-policies/{policyId}/unpin          -> ImagePolicyResponse; Editor; automatic updates resume
//   GET    /api/system/signing-key                       -> AggregatorSigningKeyResponse[] (newest first); administrators
//   POST   /api/system/signing-key/rotate                -> AggregatorSigningKeyResponse; administrators; audited
//   GET    /api/settings/image-updates                   -> ImageUpdateSettingsResponse; administrators
//   PUT    /api/settings/image-updates                   -> ImageUpdateSettingsResponse; administrators; audited
//
//   POST   /api/image-hooks/{stackId}                    -> 202 ImageUpdateResultResponse[]; CI; Authorization: Bearer <token>;
//                                                           body {"image"?, "tag"?}, both optional: no image checks every
//                                                           policy that accepts requests, a tag needs its image
//
// The hook answers 401 for an unknown stack, one without an image hook, a missing or wrong token alike; 404 when no
// policy of the image accepts requests; 400 for a tag the policy does not allow or the registry does not have; 409 for an
// older tag when the hook may not roll back; 503 when the agent is offline. A check that fails (the registry, git, the
// compose file) answers 200 or 202 with the outcome Failed and the reason: the policy shows it too.

/// <param name="Services">Only these services; empty for every service with the image.</param>
/// <param name="Poll">The aggregator reads the registry every polling interval.</param>
/// <param name="AcceptRequests">The stack's image hook may ask for a check or an exact tag.</param>
/// <param name="PinDigest">Write "tag@sha256:…"; required when the stack requires image digests.</param>
public sealed record ImagePolicySettingsRequest(
    string TagPattern,
    ImageTagOrder Order,
    IReadOnlyList<string>? Services,
    bool Poll = true,
    bool AcceptRequests = true,
    bool PinDigest = false);

/// <param name="Image">The repository without a tag, e.g. "gitea.example.com/team/app/app".</param>
public sealed record CreateImagePolicyRequest(
    string Image,
    string TagPattern,
    ImageTagOrder Order,
    IReadOnlyList<string>? Services,
    bool Poll = true,
    bool AcceptRequests = true,
    bool PinDigest = false);

/// <param name="Image">Normalized: "docker.io/library/nginx" for "nginx".</param>
/// <param name="PinnedBy">A user name, or "image hook".</param>
/// <param name="LastError">Why the last check did not finish; null after a successful one.</param>
/// <param name="Warning">Worth knowing, does not stop the policy: e.g. that ordering by creation time is expensive here.</param>
/// <param name="LatestTag">The newest tag the last check found.</param>
/// <param name="LastChangedByAdministrator">Update commits keep the compose policy exemption of the content under them only then (ADR-0015, item 7).</param>
public sealed record ImagePolicyResponse(
    Guid Id,
    Guid StackId,
    string Image,
    string TagPattern,
    ImageTagOrder Order,
    IReadOnlyList<string> Services,
    bool Poll,
    bool AcceptRequests,
    bool PinDigest,
    string? PinnedTag,
    string? PinnedBy,
    DateTimeOffset? PinnedAt,
    DateTimeOffset? LastCheckedAt,
    string? LastError,
    string? Warning,
    string? LatestTag,
    bool LastChangedByAdministrator,
    DateTimeOffset CreatedAt);

/// <param name="RequestedBy">The user who asked for the tag; null for the policy and the hook.</param>
public sealed record ImageUpdateRecordResponse(
    Guid Id,
    Guid PolicyId,
    string Commit,
    string Parent,
    string Tag,
    string? Digest,
    IReadOnlyList<ImageChangeResponse> Changes,
    ImageUpdateReason Reason,
    string? RequestedBy,
    DateTimeOffset CreatedAt);

public sealed record ImageChangeResponse(string Service, string From, string To);

/// <param name="CanChange">Whether the user may change the policies, check, pin and unpin; for hiding buttons only.</param>
/// <param name="RecentUpdates">The stack's latest update commits, newest first.</param>
public sealed record ImagePolicyListResponse(
    IReadOnlyList<ImagePolicyResponse> Policies, bool CanChange, IReadOnlyList<ImageUpdateRecordResponse> RecentUpdates);

/// <param name="CurrentTag">The tag in the compose file at the branch head.</param>
/// <param name="TargetTag">The tag selected or asked for.</param>
/// <param name="Detail">What changes, or why nothing did.</param>
/// <param name="Commit">The update commit, when one was pushed.</param>
/// <param name="DeploymentId">The deployment queued for it; null when a running or queued one deploys it.</param>
public sealed record ImageUpdateResultResponse(
    Guid PolicyId,
    string Image,
    ImageUpdateOutcome Outcome,
    string? CurrentTag,
    string? TargetTag,
    string? Detail,
    string? Commit,
    Guid? DeploymentId);

public sealed record PinImageRequest(string Tag);

/// <param name="Image">The image CI built; null for every policy of the stack that accepts requests.</param>
/// <param name="Tag">An exact tag; needs the image.</param>
public sealed record ImageHookCallRequest(string? Image, string? Tag);

/// <param name="PublicKey">To add to the git server's service user, so update commits show as verified.</param>
/// <param name="RetiredAt">Null for the active key.</param>
public sealed record AggregatorSigningKeyResponse(Guid Id, string PublicKey, string Fingerprint, DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt);

/// <param name="PollingIntervalMinutes">1 to 1440.</param>
/// <param name="PollingEnabledLocked">The aggregator's environment sets the field, so it cannot be changed here.</param>
/// <param name="PollingIntervalLocked">As <paramref name="PollingEnabledLocked"/>, for the interval.</param>
public sealed record ImageUpdateSettingsResponse(bool PollingEnabled, int PollingIntervalMinutes, bool PollingEnabledLocked, bool PollingIntervalLocked);

public sealed record UpdateImageUpdateSettingsRequest(bool PollingEnabled, int PollingIntervalMinutes);
