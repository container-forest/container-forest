namespace ContainerForest.Aggregator.Api.Contracts;

// SOPS files for the git filter of the CLI (ADR-0010): people keep plain text in their clones, the repository keeps
// standard sops files. Content travels as base64.
//
//   POST /api/sops/decrypt -> SopsContentResponse; 403 when the token may not decrypt secrets or no recipient of the
//                             file is a key of an environment where the user is an Editor (administrators: any);
//                             422 when no environment key is a recipient or sops fails; 400 when it is not a sops file
//   POST /api/sops/encrypt -> SopsContentResponse; any user who may change things (not a read-only token); 400 when
//                             .sops.yaml has no usable rule for the path (age recipients only) or the file is encrypted
//                             already; 422 when sops fails
//
// Both are rate-limited per user. The aggregator logs who decrypted which path, never the content.

/// <param name="Path">The file's path relative to the repository root; its extension tells the format, as for sops.</param>
/// <param name="Content">The encrypted file, base64.</param>
public sealed record SopsDecryptRequest(string Path, byte[] Content)
{
    public override string ToString() => $"{nameof(SopsDecryptRequest)} {{ Path = {Path} }}";
}

/// <param name="Path">The file's path relative to the repository root, matched against path_regex of the rules.</param>
/// <param name="SopsConfig">The repository's .sops.yaml.</param>
/// <param name="Content">The plain file, base64.</param>
public sealed record SopsEncryptRequest(string Path, string SopsConfig, byte[] Content)
{
    public override string ToString() => $"{nameof(SopsEncryptRequest)} {{ Path = {Path} }}";
}

/// <param name="Content">The decrypted or encrypted file, base64.</param>
public sealed record SopsContentResponse(byte[] Content)
{
    public override string ToString() => nameof(SopsContentResponse);
}
