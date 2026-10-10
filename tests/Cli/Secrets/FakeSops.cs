using System.Text;
using ContainerForest.Cli.Secrets;

namespace ContainerForest.Cli.Tests.Secrets;

/// <summary>"Encrypts" dotenv files by wrapping them in sops metadata, with a counter so each encryption differs, as with sops.</summary>
internal sealed class FakeSops : ISopsBackend
{
    private int encryptions;

    public string? Refusal { get; set; }

    public List<string> Encrypted { get; } = [];

    public Task<byte[]> DecryptAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        if (Refusal is not null)
        {
            throw new SopsBackendException(Refusal);
        }

        var text = Encoding.UTF8.GetString(content);
        return Task.FromResult(Encoding.UTF8.GetBytes(text[(text.IndexOf(':', StringComparison.Ordinal) + 1)..text.IndexOf("\nsops_version=", StringComparison.Ordinal)]));
    }

    public Task<byte[]> EncryptAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        if (Refusal is not null)
        {
            throw new SopsBackendException(Refusal);
        }

        Encrypted.Add(path);
        return Task.FromResult(Wrap(Encoding.UTF8.GetString(content), ++encryptions));
    }

    public static byte[] Wrap(string plain, int version = 0) =>
        Encoding.UTF8.GetBytes($"ENC{version}:{plain}\nsops_version=3.13.3\n");
}
