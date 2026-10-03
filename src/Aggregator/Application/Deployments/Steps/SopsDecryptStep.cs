using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Deployments.Steps;

/// <summary>
/// Decrypts the stack's SOPS files with the age keys of the target environment only, so a file encrypted for another
/// environment fails here instead of reaching this host. Decrypted files are marked secret: the agent keeps them in
/// memory (tmpfs). The log names the files, never their content.
/// </summary>
public sealed class SopsDecryptStep(IAgeKeyRepository keys, ISecretProtector protector, ISopsDecryptor sops) : IDeployStep
{
    public string Name => "Decrypt SOPS files";

    public async Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
    {
        var source = context.Source;
        var encrypted = source.Files.Where(f => SopsFile.IsEncrypted(f.Path, f.Content)).ToList();
        if (encrypted.Count == 0)
        {
            context.Info("No SOPS-encrypted files.");
            return;
        }

        var environment = context.Environment;
        var environmentKeys = await keys.ListByEnvironmentAsync(environment.Id, cancellationToken);
        if (environmentKeys.Count == 0)
        {
            throw new DeploymentFailedException(
                $"{Describe(encrypted)} encrypted with SOPS, but environment '{environment.Name}' has no age key. " +
                "An administrator creates one on the environment's keys page; then add its public key to .sops.yaml " +
                "and re-encrypt the files (sops updatekeys).");
        }

        var identities = environmentKeys.Select(Unprotect).ToList();
        var decrypted = new Dictionary<StackFilePath, byte[]>();
        foreach (var file in encrypted)
        {
            try
            {
                decrypted[file.Path] = await sops.DecryptAsync(file.Path, file.Content, identities, cancellationToken);
            }
            catch (SopsDecryptionException ex)
            {
                throw new DeploymentFailedException($"Could not decrypt '{file.Path}': {ex.Message}");
            }
        }

        context.Source = source with
        {
            Files = source.Files
                .Select(f => decrypted.TryGetValue(f.Path, out var content) ? f with { Content = content, IsSecret = true } : f)
                .ToList(),
        };
        context.Info(
            $"Decrypted {Describe(encrypted)} with the {environmentKeys.Count} age key(s) of environment '{environment.Name}'; " +
            "the agent keeps them in memory only.");
    }

    private static string Describe(List<SourceFile> files) =>
        $"{files.Count} file(s) ({string.Join(", ", files.Select(f => f.Path.Value))})";

    private string Unprotect(AgeKey key)
    {
        try
        {
            return protector.Unprotect(SecretPurpose.AgeIdentity, key.ProtectedIdentity);
        }
        catch (SecretUnreadableException)
        {
            throw new DeploymentFailedException(
                $"The private part of age key {key.Recipient} cannot be decrypted (the aggregator's key ring was lost or changed). " +
                "Create a new key, re-encrypt the files for it and delete this one.");
        }
    }
}
