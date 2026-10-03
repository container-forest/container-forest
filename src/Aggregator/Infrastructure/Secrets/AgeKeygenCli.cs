using System.Text;
using ContainerForest.Aggregator.Application.Secrets;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Infrastructure.Secrets;

/// <summary>Generates X25519 age keys with the age-keygen binary; its output is parsed, never logged.</summary>
internal sealed class AgeKeygenCli(IOptions<SopsOptions> options) : IAgeKeyGenerator
{
    private const string PublicKeyComment = "# public key: ";
    private const string IdentityPrefix = "AGE-SECRET-KEY-1";

    public async Task<GeneratedAgeKey> GenerateAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var result = await CliProcess.RunAsync(
            settings.AgeKeygenPath, [], new Dictionary<string, string>(), stdin: null, settings.Timeout, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"age-keygen failed with exit code {result.ExitCode}: {result.Stderr.Trim()}");
        }

        return Parse(Encoding.UTF8.GetString(result.Stdout));
    }

    /// <summary>
    /// age-keygen prints "# created: …", "# public key: age1…" and the identity line. The public key is also on
    /// stderr; the comment in stdout is enough and keeps both parts from one place.
    /// </summary>
    internal static GeneratedAgeKey Parse(string output)
    {
        string? recipient = null;
        string? identity = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(PublicKeyComment, StringComparison.Ordinal))
            {
                recipient = line[PublicKeyComment.Length..].Trim();
            }
            else if (line.StartsWith(IdentityPrefix, StringComparison.Ordinal))
            {
                identity = line;
            }
        }

        // The message must not quote the output: it holds the private key.
        return recipient is not null && identity is not null
            ? new GeneratedAgeKey(recipient, identity)
            : throw new InvalidOperationException("age-keygen printed no key pair in the expected format.");
    }
}
