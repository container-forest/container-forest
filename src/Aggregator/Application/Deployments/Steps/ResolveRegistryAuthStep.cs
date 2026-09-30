using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Registries;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;

namespace ContainerForest.Aggregator.Application.Deployments.Steps;

/// <summary>
/// Adds to the bundle the environment's registry logins that the stack's images need. Only those are sent:
/// whoever controls the host can read what its agent receives.
/// </summary>
public sealed class ResolveRegistryAuthStep(IRegistryCredentialRepository credentials, ISecretProtector protector) : IDeployStep
{
    public string Name => "Resolve registry credentials";

    public async Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
    {
        var available = await credentials.ListByEnvironmentAsync(context.Environment.Id, cancellationToken);
        if (available.Count == 0)
        {
            return;
        }

        var needed = Select(available, context.Compose);
        if (needed.Count == 0)
        {
            return;
        }

        context.Bundle = context.Bundle with { RegistryLogins = needed.Select(Decrypt).ToList() };
        context.Info($"Registry credentials: {string.Join(", ", needed.Select(c => $"{c.Server} ({c.Username})"))}.");
    }

    private static List<RegistryCredential> Select(IReadOnlyList<RegistryCredential> available, ComposeFile compose)
    {
        // Where an image comes from is unknown for "${REGISTRY}/app" (interpolated on the host) and for a service
        // that takes its image from "extends"; the agent then gets all of the environment's logins.
        var images = new List<ImageReference>();
        foreach (var service in compose.Services)
        {
            if (service.Image is null || service.Image.Contains('$', StringComparison.Ordinal)
                || !ImageReference.TryParse(service.Image, out var image))
            {
                return [.. available];
            }

            images.Add(image);
        }

        return available.Where(c => images.Exists(c.Serves)).ToList();
    }

    private RegistryLogin Decrypt(RegistryCredential credential)
    {
        try
        {
            return new RegistryLogin(
                RegistryHost.ConfigKey(credential.Server),
                credential.Username,
                protector.Unprotect(SecretPurpose.RegistryPassword, credential.ProtectedPassword));
        }
        catch (SecretUnreadableException)
        {
            throw new DeploymentFailedException(
                $"The stored password for registry '{credential.Server}' cannot be decrypted; set the credentials for this environment again.");
        }
    }
}
