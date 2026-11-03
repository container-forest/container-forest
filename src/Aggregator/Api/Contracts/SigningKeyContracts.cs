namespace ContainerForest.Aggregator.Api.Contracts;

// Signing keys: public SSH keys whose commit signatures endorse deployments of stacks that require it (ADR-0011).
//
//   GET    /api/me/signing-keys                    -> SigningKeyResponse[], newest first; a session only, not a token
//   POST   /api/me/signing-keys                    -> 201 SigningKeyResponse; a session only
//   PUT    /api/me/signing-keys/{id}               -> SigningKeyResponse; name and environments; a session only
//   DELETE /api/me/signing-keys/{id}               -> 204; a session only
//   GET    /api/users/{id}/signing-keys            -> SigningKeyResponse[]; administrators
//   POST   /api/users/{id}/signing-keys            -> 201 SigningKeyResponse; administrators, e.g. for a CI account
//   DELETE /api/users/{id}/signing-keys/{keyId}    -> 204; administrators
//
// A key endorses a commit for a stack when its user may deploy that stack, within the key's environments: like the
// limits of an API token, they only take rights away. A key that is already added, by anyone, is 409; one that is
// not an accepted public SSH key (ssh-ed25519, ecdsa, FIDO sk- keys, RSA of 2048 bits or more) is 400.

/// <param name="PublicKey">"type base64", without the comment.</param>
/// <param name="Fingerprint">"SHA256:…", as ssh-keygen -l shows it.</param>
/// <param name="HardwareBacked">A FIDO key (sk-…): signing needs a touch on the authenticator.</param>
/// <param name="EnvironmentIds">The environments the key endorses deployments in; null for all of them.</param>
/// <param name="AddedBy">The administrator who added the key for its user; null when the user added it.</param>
/// <param name="LastUsedAt">When the key last endorsed a deployment, precise to about a minute.</param>
public sealed record SigningKeyResponse(
    Guid Id,
    string Name,
    string PublicKey,
    string Fingerprint,
    bool HardwareBacked,
    IReadOnlyList<Guid>? EnvironmentIds,
    string? AddedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt);

/// <param name="PublicKey">The content of the .pub file, e.g. "ssh-ed25519 AAAA… user@laptop".</param>
/// <param name="EnvironmentIds">Null for every environment; otherwise at least one environment the key's user can see.</param>
public sealed record AddSigningKeyRequest(string Name, string PublicKey, IReadOnlyList<Guid>? EnvironmentIds);

/// <param name="EnvironmentIds">Null for every environment; otherwise at least one environment the user can see.</param>
public sealed record UpdateSigningKeyRequest(string Name, IReadOnlyList<Guid>? EnvironmentIds);
