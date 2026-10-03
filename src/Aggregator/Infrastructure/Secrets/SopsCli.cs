using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Infrastructure.Secrets;

/// <summary>
/// Decrypts with the sops binary. The encrypted file goes in through stdin and the plain text comes back through
/// stdout, so neither touches the disk; the private keys go in SOPS_AGE_KEY of the child process only.
/// </summary>
internal sealed partial class SopsCli(IOptions<SopsOptions> options, ILogger<SopsCli> logger) : ISopsDecryptor
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
        throw new SopsDecryptionException(result.ExitCode == CouldNotRetrieveKey
            ? "none of this environment's age keys is a recipient of the file. Add the environment's public key to " +
              ".sops.yaml and re-encrypt the file (sops updatekeys)."
            : $"sops failed with exit code {result.ExitCode}: {FirstLine(result.Stderr)}");
    }

    private static string FirstLine(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length <= MaxErrorLength ? line : line[..MaxErrorLength] + "…";
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "sops cannot be started; install it or set Sops:SopsPath")]
    private partial void LogUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "sops could not decrypt {Path} (exit code {ExitCode}): {Stderr}")]
    private partial void LogFailed(string path, int exitCode, string stderr);
}
