using System.Text;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Infrastructure.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Infrastructure.Deployments;

/// <summary>Configuration section "Signatures" (env: CF_SIGNATURES__...).</summary>
public sealed class SignatureOptions
{
    public const string SectionName = "Signatures";

    /// <summary>The ssh-keygen binary (openssh-client); a bare name is looked up in PATH. The aggregator image has it.</summary>
    public string SshKeygenPath { get; set; } = "ssh-keygen";

    /// <summary>How long one verification may take.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    public bool IsValid() => Timeout > TimeSpan.Zero && SshKeygenPath.Length > 0;
}

/// <summary>
/// Verifies commit signatures with <c>ssh-keygen -Y verify -n git</c> (ADR-0011), the same check git itself runs
/// for <c>git verify-commit</c>. The allowed signers file holds only the key the signature names, already matched
/// by fingerprint; ssh-keygen then checks the cryptography and the namespace. Everything written to disk is public:
/// a public key and a signature.
/// </summary>
internal sealed partial class SshKeygenSignatureVerifier(
    IOptions<SignatureOptions> options, ILogger<SshKeygenSignatureVerifier> logger) : ICommitSignatureVerifier
{
    private const string Principal = "signer";

    public async Task<bool> VerifyAsync(
        SshPublicKey key, SshSignature signature, byte[] signedData, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("cf-sig-");
        try
        {
            var allowedSigners = Path.Combine(directory.FullName, "allowed_signers");
            var signatureFile = Path.Combine(directory.FullName, "commit.sig");
            await File.WriteAllTextAsync(
                allowedSigners, $"{Principal} namespaces=\"{SshSignature.GitNamespace}\" {key.OpenSsh}\n", Encoding.ASCII, cancellationToken);
            await File.WriteAllTextAsync(signatureFile, signature.Armored, Encoding.ASCII, cancellationToken);

            CliResult result;
            try
            {
                result = await CliProcess.RunAsync(
                    options.Value.SshKeygenPath,
                    ["-Y", "verify", "-f", allowedSigners, "-I", Principal, "-n", SshSignature.GitNamespace, "-s", signatureFile],
                    new Dictionary<string, string>(),
                    signedData,
                    options.Value.Timeout,
                    cancellationToken);
            }
            catch (CliUnavailableException ex)
            {
                LogUnavailable(ex);
                throw new DeploymentFailedException("The aggregator cannot check commit signatures: ssh-keygen is not installed.");
            }
            catch (TimeoutException)
            {
                throw new DeploymentFailedException("Checking the commit signature took too long.");
            }

            if (result.ExitCode != 0)
            {
                LogRejected(key.Fingerprint, FirstLine(result.Stderr));
            }

            return result.ExitCode == 0;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    [LoggerMessage(Level = LogLevel.Error, Message = "ssh-keygen cannot be started; commit signatures cannot be checked")]
    private partial void LogUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A commit signature by key {Fingerprint} did not verify: {Reason}")]
    private partial void LogRejected(string fingerprint, string reason);
}
