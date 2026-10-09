using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Api;

public sealed partial class ForestApi
{
    /// <summary>Environments the user can see, by name.</summary>
    public async Task<IReadOnlyList<EnvironmentResponse>> ListEnvironmentsAsync(CancellationToken cancellationToken) =>
        await Api.Environments.GetAsync(cancellationToken: cancellationToken) ?? [];

    /// <summary>
    /// The environment a command works in: the one named, otherwise the context's default, otherwise the only one the
    /// user can see.
    /// </summary>
    /// <exception cref="CliException">None is named and there is no single one to pick, or the named one is not visible.</exception>
    public async Task<EnvironmentResponse> ResolveEnvironmentAsync(string? requested, string? contextDefault, CancellationToken cancellationToken)
    {
        var environments = await ListEnvironmentsAsync(cancellationToken);
        if ((requested ?? contextDefault) is { } nameOrId)
        {
            return Match(environments, nameOrId)
                ?? throw new CliException($"There is no environment '{nameOrId}' you can see; see 'forest env list'.", ExitCodes.Denied);
        }

        return environments.Count switch
        {
            1 => environments[0],
            0 => throw new CliException("You can see no environments.", ExitCodes.Denied),
            _ => throw new CliException(
                "Several environments: choose one with --env, or set a default with 'forest context set-env <environment>'.",
                ExitCodes.Usage),
        };
    }

    public async Task<EnvironmentCreatedResponse> CreateEnvironmentAsync(string name, bool isPrivate, CancellationToken cancellationToken) =>
        Required(
            await Api.Environments.PostAsync(new CreateEnvironmentRequest { Name = name, IsPrivate = isPrivate }, cancellationToken: cancellationToken),
            "created environment");

    public async Task<EnvironmentResponse> UpdateEnvironmentAsync(Guid id, string name, bool isPrivate, CancellationToken cancellationToken) =>
        Required(
            await Api.Environments[id].PutAsync(new UpdateEnvironmentRequest { Name = name, IsPrivate = isPrivate }, cancellationToken: cancellationToken),
            "environment");

    public Task RemoveEnvironmentAsync(Guid id, CancellationToken cancellationToken) =>
        Api.Environments[id].DeleteAsync(cancellationToken: cancellationToken);

    public async Task<EnrollmentTokenResponse> IssueEnrollmentTokenAsync(Guid id, CancellationToken cancellationToken) =>
        Required(await Api.Environments[id].EnrollmentToken.PostAsync(cancellationToken: cancellationToken), "enrollment token");

    public async Task RevokeAgentAsync(Guid id, CancellationToken cancellationToken) =>
        await Api.Environments[id].RevokeAgent.PostAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<AgeKeyResponse>> ListAgeKeysAsync(Guid environmentId, CancellationToken cancellationToken) =>
        await Api.Environments[environmentId].AgeKeys.GetAsync(cancellationToken: cancellationToken) ?? [];

    public async Task<AgeKeyResponse> CreateAgeKeyAsync(Guid environmentId, CancellationToken cancellationToken) =>
        Required(await Api.Environments[environmentId].AgeKeys.PostAsync(cancellationToken: cancellationToken), "age key");

    public Task RemoveAgeKeyAsync(Guid environmentId, Guid keyId, CancellationToken cancellationToken) =>
        Api.Environments[environmentId].AgeKeys[keyId].DeleteAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<RegistryCredentialResponse>> ListRegistryCredentialsAsync(Guid environmentId, CancellationToken cancellationToken) =>
        await Api.Environments[environmentId].RegistryCredentials.GetAsync(cancellationToken: cancellationToken) ?? [];

    public async Task<RegistryCredentialResponse> SetRegistryCredentialAsync(
        Guid environmentId, string server, string username, string password, CancellationToken cancellationToken) =>
        Required(
            await Api.Environments[environmentId].RegistryCredentials.PutAsync(
                new SetRegistryCredentialRequest { Server = server, Username = username, Password = password },
                cancellationToken: cancellationToken),
            "registry credential");

    public Task RemoveRegistryCredentialAsync(Guid environmentId, Guid credentialId, CancellationToken cancellationToken) =>
        Api.Environments[environmentId].RegistryCredentials[credentialId].DeleteAsync(cancellationToken: cancellationToken);

    private static EnvironmentResponse? Match(IReadOnlyList<EnvironmentResponse> environments, string nameOrId) =>
        Guid.TryParse(nameOrId, out var id)
            ? environments.FirstOrDefault(e => e.Id == id)
            : environments.FirstOrDefault(e => string.Equals(e.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
}
