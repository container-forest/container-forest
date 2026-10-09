using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Api;

public sealed partial class ForestApi
{
    /// <param name="connectionId">Null for a public repository.</param>
    public async Task<RepositoryResponse> AddRepositoryAsync(string name, string url, Guid? connectionId, CancellationToken cancellationToken) =>
        Required(
            await Api.Repositories.PostAsync(
                new CreateRepositoryRequest { Name = name, Url = url, ConnectionId = connectionId }, cancellationToken: cancellationToken),
            "repository");

    public async Task<RepositoryResponse> UpdateRepositoryAsync(
        Guid id, string name, string url, Guid? connectionId, CancellationToken cancellationToken) =>
        Required(
            await Api.Repositories[id].PutAsync(
                new UpdateRepositoryRequest { Name = name, Url = url, ConnectionId = connectionId }, cancellationToken: cancellationToken),
            "repository");

    public Task RemoveRepositoryAsync(Guid id, CancellationToken cancellationToken) =>
        Api.Repositories[id].DeleteAsync(cancellationToken: cancellationToken);

    public async Task<WebhookSecretResponse> CreateRepositoryWebhookAsync(Guid id, CancellationToken cancellationToken) =>
        Required(await Api.Repositories[id].Webhook.PostAsync(cancellationToken: cancellationToken), "webhook secret");

    public async Task RemoveRepositoryWebhookAsync(Guid id, CancellationToken cancellationToken) =>
        await Api.Repositories[id].Webhook.DeleteAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<GitConnectionResponse>> ListGitConnectionsAsync(CancellationToken cancellationToken) =>
        await Api.GitConnections.GetAsync(cancellationToken: cancellationToken) ?? [];

    /// <summary>A git connection by id or name (names are unique).</summary>
    /// <exception cref="CliException">There is no such connection the user can see.</exception>
    public async Task<GitConnectionResponse> FindGitConnectionAsync(string nameOrId, CancellationToken cancellationToken)
    {
        var connections = await ListGitConnectionsAsync(cancellationToken);
        return connections.FirstOrDefault(c => c.Id?.ToString() == nameOrId || string.Equals(c.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliException($"There is no git connection '{nameOrId}' you can see; see 'forest connection list'.", ExitCodes.Denied);
    }

    /// <param name="environmentIds">Null for every environment, present and future.</param>
    /// <param name="token">Null for a connection without credentials.</param>
    public async Task<GitConnectionResponse> AddGitConnectionAsync(
        string name, string urlPrefix, IReadOnlyCollection<Guid>? environmentIds, string? userName, string? token, CancellationToken cancellationToken) =>
        Required(
            await Api.GitConnections.PostAsync(
                new CreateGitConnectionRequest
                {
                    Name = name,
                    UrlPrefix = urlPrefix,
                    AllEnvironments = environmentIds is null,
                    EnvironmentIds = environmentIds?.Select(id => (Guid?)id).ToList(),
                    AccessToken = token is null ? null : new GitAccessTokenRequest { UserName = userName, Token = token },
                },
                cancellationToken: cancellationToken),
            "git connection");

    public async Task<GitConnectionResponse> UpdateGitConnectionAsync(
        Guid id, string name, string urlPrefix, IReadOnlyCollection<Guid>? environmentIds, CancellationToken cancellationToken) =>
        Required(
            await Api.GitConnections[id].PutAsync(
                new UpdateGitConnectionRequest
                {
                    Name = name,
                    UrlPrefix = urlPrefix,
                    AllEnvironments = environmentIds is null,
                    EnvironmentIds = environmentIds?.Select(id => (Guid?)id).ToList(),
                },
                cancellationToken: cancellationToken),
            "git connection");

    public Task RemoveGitConnectionAsync(Guid id, CancellationToken cancellationToken) =>
        Api.GitConnections[id].DeleteAsync(cancellationToken: cancellationToken);

    public async Task<GitConnectionResponse> SetGitConnectionTokenAsync(Guid id, string? userName, string token, CancellationToken cancellationToken) =>
        Required(
            await Api.GitConnections[id].AccessToken.PutAsync(
                new GitAccessTokenRequest { UserName = userName, Token = token }, cancellationToken: cancellationToken),
            "git connection");

    public async Task RemoveGitConnectionTokenAsync(Guid id, CancellationToken cancellationToken) =>
        await Api.GitConnections[id].AccessToken.DeleteAsync(cancellationToken: cancellationToken);

    public async Task<GitConnectionWebhookSecretResponse> CreateGitConnectionWebhookAsync(Guid id, CancellationToken cancellationToken) =>
        Required(await Api.GitConnections[id].Webhook.PostAsync(cancellationToken: cancellationToken), "webhook secret");

    public async Task RemoveGitConnectionWebhookAsync(Guid id, CancellationToken cancellationToken) =>
        await Api.GitConnections[id].Webhook.DeleteAsync(cancellationToken: cancellationToken);
}
