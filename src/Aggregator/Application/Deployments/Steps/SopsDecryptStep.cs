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
/// <remarks>
/// A file that .gitattributes puts under the forest git filter but that is not encrypted was committed in plain text
/// from a clone without the filter (ADR-0010). The deployment fails: deploying it would hide the leak, and the secret
/// is in the repository's history either way.
/// </remarks>
public sealed class SopsDecryptStep(IAgeKeyRepository keys, ISecretProtector protector, ISopsDecryptor sops) : IDeployStep
{
    public string Name => "Decrypt SOPS files";

    public async Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
    {
        var source = context.Source;
        var encrypted = source.Files.Where(f => SopsFile.IsEncrypted(f.Path, f.Content)).ToList();
        EnsureFilteredFilesEncrypted(context, encrypted);
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

    private static void EnsureFilteredFilesEncrypted(DeployContext context, List<SourceFile> encrypted)
    {
        var directory = context.Stack.ComposePath.Directory;
        string InRepository(string path) => directory.Length == 0 ? path : $"{directory}/{path}";

        var source = context.Source;
        var attributes = new GitAttributes((source.OuterAttributes ?? []).Concat(source.Files
            .Where(f => f.Path.FileName == GitAttributes.FileName)
            .Select(f => new GitAttributesFile(InRepository(f.Path.Directory).TrimEnd('/'), System.Text.Encoding.UTF8.GetString(f.Content)))));
        var plain = source.Files
            .Where(f => !encrypted.Contains(f) && attributes.IsFiltered(InRepository(f.Path.Value)))
            .Select(f => f.Path.Value)
            .ToList();
        if (plain.Count > 0)
        {
            throw new DeploymentFailedException(
                $"{plain.Count} file(s) marked filter=forest in .gitattributes are not encrypted ({string.Join(", ", plain)}): " +
                "they were committed in plain text from a clone without the forest git filter. Treat the secrets as leaked: " +
                "change them, then run 'forest git setup' in the clone and commit the files again.");
        }
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
