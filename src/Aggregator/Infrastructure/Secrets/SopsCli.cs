using System.Text.Json;
using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Infrastructure.Secrets;

/// <summary>
/// Decrypts and encrypts with the sops binary. The file goes in through stdin and the result comes back through
/// stdout, so plain text never touches the disk; the private keys go in SOPS_AGE_KEY of the child process only.
/// </summary>
internal sealed partial class SopsCli(IOptions<SopsOptions> options, ILogger<SopsCli> logger) : ISopsDecryptor, ISopsEncryptor
{
    // sops' exit code when no master key could decrypt the data key (cmd/sops/codes.CouldNotRetrieveKey).
    private const int CouldNotRetrieveKey = 128;

    private const int MaxErrorLength = 300;

    public async Task<byte[]> DecryptAsync(
        StackFilePath path, byte[] content, IReadOnlyList<string> identities, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        CliResult result;
        try
        {
            // --filename-override makes sops pick the format from the stack path, as it would for the file itself.
            result = await CliProcess.RunAsync(
                settings.SopsPath,
                ["decrypt", "--filename-override", path.Value, "/dev/stdin"],
                new Dictionary<string, string> { ["SOPS_AGE_KEY"] = string.Join('\n', identities) },
                content,
                settings.Timeout,
                cancellationToken);
        }
        catch (CliUnavailableException ex)
        {
            LogUnavailable(ex);
            throw new SopsDecryptionException("sops is not installed on the aggregator.");
        }
        catch (TimeoutException)
        {
            throw new SopsDecryptionException($"sops did not finish in {settings.Timeout.TotalSeconds:0} s.");
        }

        if (result.ExitCode == 0)
        {
            return result.Stdout;
        }

        // sops' errors carry key ids, recipients and hashes, never the plain text it could not produce.
        LogFailed(path.Value, result.ExitCode, result.Stderr);
        throw new SopsDecryptionException(result.ExitCode switch
        {
            CouldNotRetrieveKey =>
                "none of this environment's age keys is a recipient of the file. Add the environment's public key to " +
                ".sops.yaml and re-encrypt the file (sops updatekeys).",

            // sops never writes CR, so CRLF means a tool converted the file, usually git on Windows (core.autocrlf).
            _ when HasCrLf(content) =>
                $"the file has CRLF line endings, which sops cannot read (sops: {FirstLine(result.Stderr)}). Convert it " +
                "to LF; on Windows git adds CR unless .gitattributes sets eol=lf for it ('forest secrets fix-eol').",
            _ => $"sops failed with exit code {result.ExitCode}: {FirstLine(result.Stderr)}",
        });
    }

    private static bool HasCrLf(byte[] content) => content.AsSpan().IndexOf("\r\n"u8) >= 0;

    /// <summary>
    /// Encrypts with a configuration written from the rule alone: the repository's own .sops.yaml never reaches sops,
    /// so it cannot name KMS, Vault or PGP keys for the aggregator to call out to. The file holds public keys only.
    /// </summary>
    public async Task<byte[]> EncryptAsync(
        StackFilePath path, byte[] content, SopsEncryptionRule rule, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var config = Path.Combine(Path.GetTempPath(), $"cf-sops-{Guid.NewGuid():N}.json");
        CliResult result;
        try
        {
            await File.WriteAllTextAsync(config, ConfigFor(rule), cancellationToken);
            result = await CliProcess.RunAsync(
                settings.SopsPath,
                ["--config", config, "encrypt", "--filename-override", path.Value, "/dev/stdin"],
                new Dictionary<string, string>(),
                content,
                settings.Timeout,
                cancellationToken);
        }
        catch (CliUnavailableException ex)
        {
            LogUnavailable(ex);
            throw new SopsEncryptionException("sops is not installed on the aggregator.");
        }
        catch (TimeoutException)
        {
            throw new SopsEncryptionException($"sops did not finish in {settings.Timeout.TotalSeconds:0} s.");
        }
        finally
        {
            File.Delete(config);
        }

        if (result.ExitCode == 0)
        {
            return result.Stdout;
        }

        // A parse error may quote a line of the input, which is plain text here: only the exit code goes to the log.
        LogEncryptFailed(path.Value, result.ExitCode);
        throw new SopsEncryptionException(
            $"sops failed with exit code {result.ExitCode}; check that '{path}' is valid for its format.");
    }

    /// <summary>One creation rule without path_regex: it applies to whatever file sops is given. JSON is YAML too.</summary>
    internal static string ConfigFor(SopsEncryptionRule rule)
    {
        var creationRule = new Dictionary<string, object> { ["age"] = string.Join(',', rule.AgeRecipients) };
        AddIfSet(creationRule, "encrypted_regex", rule.EncryptedRegex);
        AddIfSet(creationRule, "unencrypted_regex", rule.UnencryptedRegex);
        AddIfSet(creationRule, "encrypted_suffix", rule.EncryptedSuffix);
        AddIfSet(creationRule, "unencrypted_suffix", rule.UnencryptedSuffix);
        AddIfSet(creationRule, "encrypted_comment_regex", rule.EncryptedCommentRegex);
        AddIfSet(creationRule, "unencrypted_comment_regex", rule.UnencryptedCommentRegex);
        if (rule.MacOnlyEncrypted)
        {
            creationRule["mac_only_encrypted"] = true;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object> { ["creation_rules"] = new[] { creationRule } });
    }

    private static void AddIfSet(Dictionary<string, object> rule, string key, string? value)
    {
        if (value is not null)
        {
            rule[key] = value;
        }
    }

    private static string FirstLine(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length <= MaxErrorLength ? line : line[..MaxErrorLength] + "…";
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "sops cannot be started; install it or set Sops:SopsPath")]
    private partial void LogUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "sops could not encrypt {Path} (exit code {ExitCode})")]
    private partial void LogEncryptFailed(string path, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "sops could not decrypt {Path} (exit code {ExitCode}): {Stderr}")]
    private partial void LogFailed(string path, int exitCode, string stderr);
}
