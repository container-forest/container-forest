using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Api;

public sealed partial class ForestApi
{
    /// <summary>The aggregator decrypts a SOPS file with the keys of the environments where the user is an Editor.</summary>
    public async Task<byte[]> DecryptSopsFileAsync(string path, byte[] content, CancellationToken cancellationToken) =>
        Required(
            (await Api.Sops.Decrypt.PostAsync(new SopsDecryptRequest { Path = path, Content = content }, cancellationToken: cancellationToken))?.Content,
            "decrypted content");

    /// <summary>The aggregator encrypts a file for the rule of <paramref name="sopsConfig"/> that matches the path.</summary>
    public async Task<byte[]> EncryptSopsFileAsync(string path, string sopsConfig, byte[] content, CancellationToken cancellationToken) =>
        Required(
            (await Api.Sops.Encrypt.PostAsync(
                new SopsEncryptRequest { Path = path, SopsConfig = sopsConfig, Content = content }, cancellationToken: cancellationToken))?.Content,
            "encrypted content");
}
