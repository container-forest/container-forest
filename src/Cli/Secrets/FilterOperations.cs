namespace ContainerForest.Cli.Secrets;

/// <param name="Success">False makes git report the filter's failure for the file.</param>
/// <param name="Content">What git gets for the file; empty on failure.</param>
internal sealed record FilterResult(bool Success, byte[] Content)
{
    public static FilterResult Done(byte[] content) => new(true, content);

    public static FilterResult Failed { get; } = new(false, []);
}

/// <summary>What the filter needs: where to decrypt and encrypt, the cache, and where to tell the person.</summary>
/// <param name="Decryptors">Tried in order; the first that decrypts wins.</param>
internal sealed record FilterServices(
    IReadOnlyList<ISopsBackend> Decryptors, ISopsBackend Encryptor, FilterCache? Cache, TextWriter Error)
{
    /// <summary>Reasons already told: a checkout of many files without access says why once, not per file.</summary>
    public HashSet<string> ToldReasons { get; } = [];
}

/// <summary>
/// One conversion git asks the filter for (Template Method): a file that needs no conversion passes through as it is,
/// anything else is converted by the subclass.
/// </summary>
internal abstract class FilterOperation(FilterServices services)
{
    protected FilterServices Services { get; } = services;

    public async Task<FilterResult> RunAsync(string path, byte[] content, CancellationToken cancellationToken) =>
        NeedsConversion(path, content) ? await ConvertAsync(path, content, cancellationToken) : FilterResult.Done(content);

    protected abstract bool NeedsConversion(string path, byte[] content);

    protected abstract Task<FilterResult> ConvertAsync(string path, byte[] content, CancellationToken cancellationToken);

    /// <summary>The first decryptor that can decrypt the file; null, with the reasons told, when none can.</summary>
    protected async Task<byte[]?> TryDecryptAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        foreach (var decryptor in Services.Decryptors)
        {
            try
            {
                return await decryptor.DecryptAsync(path, content, cancellationToken);
            }
            catch (SopsBackendException exception)
            {
                reasons.Add(exception.Message);
            }
        }

        var reason = string.Join("; ", reasons);
        // An older aggregator, or local sops, does not say so itself.
        if (SopsFiles.HasCrLf(content) && !reason.Contains("CRLF", StringComparison.Ordinal))
        {
            reason += " (the file reaches the filter with CRLF line endings: set eol=lf for it in .gitattributes, 'forest secrets fix-eol')";
        }

        await Services.Error.WriteLineAsync(Services.ToldReasons.Add(reason)
            ? $"forest: {path} stays encrypted: {reason}"
            : $"forest: {path} stays encrypted.");
        return null;
    }
}

/// <summary>
/// git checkout: the encrypted file from git becomes plain text in the work tree, when the user may decrypt it.
/// Otherwise the file stays encrypted, and the checkout goes on: a person without access still gets a working clone.
/// </summary>
internal sealed class SmudgeOperation(FilterServices services) : FilterOperation(services)
{
    protected override bool NeedsConversion(string path, byte[] content) => SopsFiles.IsEncrypted(path, content);

    protected override async Task<FilterResult> ConvertAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        if (await TryDecryptAsync(path, content, cancellationToken) is not { } plain)
        {
            return FilterResult.Done(content);
        }

        // Clean will turn this plain text back into exactly this ciphertext while it does not change.
        Services.Cache?.Remember(path, plain, content);
        return FilterResult.Done(plain);
    }
}

/// <summary>
/// git add, status, diff: the plain text of the work tree becomes the encrypted file git stores. Unchanged plain text
/// gets back the ciphertext it came from; a file that is already encrypted (no access, or encrypted by hand) passes as
/// it is. Any failure fails the operation: plain text must never reach git.
/// </summary>
internal sealed class CleanOperation(FilterServices services) : FilterOperation(services)
{
    protected override bool NeedsConversion(string path, byte[] content) => !SopsFiles.IsEncrypted(path, content);

    protected override async Task<FilterResult> ConvertAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        // Before the cache too: the same text saved with CRLF is unchanged.
        content = SopsFiles.PlainForSops(path, content);
        if (Services.Cache?.Find(path, content) is { } unchanged)
        {
            return FilterResult.Done(unchanged);
        }

        try
        {
            var encrypted = await Services.Encryptor.EncryptAsync(path, content, cancellationToken);
            if (!SopsFiles.IsEncrypted(path, encrypted))
            {
                throw new SopsBackendException("the result is not a SOPS file.");
            }

            Services.Cache?.Remember(path, content, encrypted);
            return FilterResult.Done(encrypted);
        }
        catch (SopsBackendException exception)
        {
            await Services.Error.WriteLineAsync($"forest: cannot encrypt {path}, so git does not take it: {exception.Message}");
            return FilterResult.Failed;
        }
    }
}
