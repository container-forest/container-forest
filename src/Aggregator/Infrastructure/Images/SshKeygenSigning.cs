using System.Text;
using ContainerForest.Aggregator.Application.Images;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Infrastructure.Deployments;
using ContainerForest.Aggregator.Infrastructure.Secrets;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Infrastructure.Images;

/// <summary>
/// Signs update commits with <c>ssh-keygen -Y sign -n git</c>, as git itself does with gpg.format=ssh (ADR-0015). The
/// private key is written for the one call to a file only the aggregator's user can read, in a directory of its own,
/// and deleted at once.
/// </summary>
internal sealed class SshKeygenCommitSigner(IOptions<SignatureOptions> options) : ICommitSigner
{
    public async Task<string> SignAsync(string privateKey, byte[] data, CancellationToken cancellationToken)
    {
        using var directory = PrivateDirectory.Create();
        var keyFile = directory.Write("key", privateKey.EndsWith('\n') ? privateKey : privateKey + "\n");
        var result = await SshKeygen.RunAsync(options.Value, ["-Y", "sign", "-n", SshSignature.GitNamespace, "-f", keyFile], data, cancellationToken);
        var signature = Encoding.ASCII.GetString(result.Stdout);
        return result.ExitCode == 0 && signature.Contains("-----BEGIN SSH SIGNATURE-----", StringComparison.Ordinal)
            ? signature.Trim() + "\n"
            : throw new ImagePolicyException($"ssh-keygen could not sign the update commit: {result.Stderr.Split('\n', 2)[0].Trim()}");
    }
}

/// <summary>Generates the aggregator's ed25519 key with <c>ssh-keygen -t ed25519</c>.</summary>
internal sealed class SshKeygenKeyGenerator(IOptions<SignatureOptions> options) : ISshKeyGenerator
{
    public async Task<GeneratedSshKey> GenerateAsync(string comment, CancellationToken cancellationToken)
    {
        using var directory = PrivateDirectory.Create();
        var keyFile = Path.Combine(directory.Path, "key");
        var result = await SshKeygen.RunAsync(options.Value, ["-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", keyFile], null, cancellationToken);
        if (result.ExitCode != 0 || !File.Exists(keyFile) || !File.Exists(keyFile + ".pub"))
        {
            throw new ImagePolicyException($"ssh-keygen could not create the aggregator's signing key: {result.Stderr.Split('\n', 2)[0].Trim()}");
        }

        return new GeneratedSshKey(
            await File.ReadAllTextAsync(keyFile, cancellationToken),
            (await File.ReadAllTextAsync(keyFile + ".pub", cancellationToken)).Trim());
    }
}

internal static class SshKeygen
{
    public static async Task<CliResult> RunAsync(SignatureOptions options, IReadOnlyList<string> arguments, byte[]? stdin, CancellationToken cancellationToken)
    {
        try
        {
            return await CliProcess.RunAsync(options.SshKeygenPath, arguments, new Dictionary<string, string>(), stdin, options.Timeout, cancellationToken);
        }
        catch (CliUnavailableException)
        {
            throw new ImagePolicyException("The aggregator cannot sign update commits: ssh-keygen is not installed.");
        }
        catch (TimeoutException)
        {
            throw new ImagePolicyException("ssh-keygen took too long.");
        }
    }
}

/// <summary>A temporary directory only the aggregator's user can enter; deleted with everything in it.</summary>
internal sealed class PrivateDirectory : IDisposable
{
    private PrivateDirectory(string path) => Path = path;

    public string Path { get; }

    public static PrivateDirectory Create()
    {
        var info = Directory.CreateTempSubdirectory("cf-key-");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(info.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new PrivateDirectory(info.FullName);
    }

    /// <summary>Writes a file readable by the owner only; ssh-keygen refuses a private key others can read.</summary>
    public string Write(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, name);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(content);
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Nothing more can be done about a file the system refuses to delete; only the aggregator's user can read it.
        }
    }
}
