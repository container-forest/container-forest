using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;

namespace ContainerForest.Aggregator.Application.Registries;

public interface IRegistryCredentialRepository : IRepository<RegistryCredential, RegistryCredentialId>
{
    /// <summary>In server order.</summary>
    Task<IReadOnlyList<RegistryCredential>> ListByEnvironmentAsync(EnvironmentId environmentId, CancellationToken cancellationToken);

    /// <param name="server">Already normalized with <see cref="RegistryCredential.ParseServer"/>.</param>
    Task<RegistryCredential?> FindByServerAsync(EnvironmentId environmentId, string server, CancellationToken cancellationToken);
}

/// <summary>A registry credential without its password, which is never returned once set.</summary>
public sealed record RegistryCredentialSummary(
    RegistryCredentialId Id, string Server, string Username, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static RegistryCredentialSummary Of(RegistryCredential credential) => new(
        credential.Id, credential.Server, credential.Username, credential.CreatedAt, credential.UpdatedAt);
}

/// <summary>
/// Registry logins of an environment's agent. Administrators only, like enrollment: the agent that receives them
/// runs as root on its host.
/// </summary>
public sealed class RegistryCredentialService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IRegistryCredentialRepository credentials,
    ISecretProtector protector,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    public async Task<IReadOnlyList<RegistryCredentialSummary>> ListAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        await environments.GetAsync(environmentId, cancellationToken);
        return (await credentials.ListByEnvironmentAsync(environmentId, cancellationToken)).Select(RegistryCredentialSummary.Of).ToList();
    }

    /// <summary>Adds the login for <paramref name="server"/> or replaces the existing one, e.g. after rotating a token.</summary>
    /// <returns>The credential, and whether it was created rather than replaced.</returns>
    public async Task<(RegistryCredentialSummary Credential, bool Created)> SetAsync(
        EnvironmentId environmentId, string server, string username, string password, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        await environments.GetAsync(environmentId, cancellationToken);
        if (string.IsNullOrEmpty(password))
        {
            throw new DomainValidationException("Registry password must not be empty.");
        }

        var host = RegistryCredential.ParseServer(server);
        var protectedPassword = protector.Protect(SecretPurpose.RegistryPassword, password);
        var now = time.GetUtcNow();

        var credential = await credentials.FindByServerAsync(environmentId, host, cancellationToken);
        var created = credential is null;
        if (credential is null)
        {
            credential = RegistryCredential.Create(environmentId, host, username, protectedPassword, now);
            credentials.Add(credential);
        }
        else
        {
            credential.ChangeLogin(username, protectedPassword, now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (RegistryCredentialSummary.Of(credential), created);
    }

    public async Task DeleteAsync(EnvironmentId environmentId, RegistryCredentialId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var credential = await credentials.FindAsync(id, cancellationToken);
        if (credential is null || credential.EnvironmentId != environmentId)
        {
            throw new NotFoundException(nameof(RegistryCredential), id);
        }

        credentials.Remove(credential);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
