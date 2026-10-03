namespace ContainerForest.Aggregator.Api.Contracts;

// Age keys SOPS files of an environment are encrypted for. Private keys never leave the aggregator.
//
//   GET    /api/environments/{id}/age-keys          -> AgeKeyResponse[] (newest first); anyone who sees the environment
//   POST   /api/environments/{id}/age-keys          -> 201 AgeKeyResponse; administrators only
//   DELETE /api/environments/{id}/age-keys/{keyId}  -> 204; administrators only; files encrypted only for it become unreadable
//
// An environment the user may not see is 404, the same as a missing one.

/// <param name="Recipient">The public key ("age1…") to put into the repository's .sops.yaml.</param>
public sealed record AgeKeyResponse(Guid Id, string Recipient, DateTimeOffset CreatedAt);
