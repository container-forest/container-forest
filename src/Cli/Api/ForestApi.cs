using ContainerForest.Cli.Api.Generated;
using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Api;

/// <summary>
/// The aggregator's API as the commands need it (Adapter over the client Kiota generates, ADR-0008). Commands never
/// build requests themselves: the generated builders and their nullable models stay behind this class, and an answer
/// that lacks what the contract promises is an error here rather than a NullReferenceException in a command.
/// </summary>
public sealed partial class ForestApi(ForestApiClient client, Uri server)
{
    public Uri Server { get; } = server;

    private Generated.Api.ApiRequestBuilder Api => client.Api;

    public async Task<MeResponse> GetMeAsync(CancellationToken cancellationToken) =>
        Required(await Api.Me.GetAsync(cancellationToken: cancellationToken), "the signed-in user");

    /// <summary>Revokes the token the CLI uses; it stops working at once.</summary>
    public Task RevokeCurrentTokenAsync(CancellationToken cancellationToken) =>
        Api.Me.Tokens.Current.DeleteAsync(cancellationToken: cancellationToken);

    /// <param name="secrets">Ask for a token that may decrypt secrets; the person decides on the approval page.</param>
    public async Task<CliAuthorizationStartedResponse> StartLoginAsync(
        string clientName, string hostName, bool secrets, CancellationToken cancellationToken) =>
        Required(
            await Api.Cli.Authorizations.PostAsync(
                new StartCliAuthorizationRequest { ClientName = clientName, HostName = hostName, Secrets = secrets },
                cancellationToken: cancellationToken),
            "the started login");

    public async Task<CliTokenResponse> PollLoginAsync(string deviceCode, CancellationToken cancellationToken) =>
        Required(
            await Api.Cli.Token.PostAsync(new CliTokenRequest { DeviceCode = deviceCode }, cancellationToken: cancellationToken),
            "the login status");

    public async Task<IReadOnlyList<RepositoryResponse>> ListRepositoriesAsync(CancellationToken cancellationToken) =>
        await Api.Repositories.GetAsync(cancellationToken: cancellationToken) ?? [];

    /// <summary>A repository by id, name or address; names need not be unique, so an ambiguous name is an error.</summary>
    /// <exception cref="CliException">No repository, or several, match.</exception>
    public async Task<RepositoryResponse> FindRepositoryAsync(string idNameOrUrl, CancellationToken cancellationToken)
    {
        var repositories = await ListRepositoriesAsync(cancellationToken);
        if (Guid.TryParse(idNameOrUrl, out var id))
        {
            return repositories.FirstOrDefault(r => r.Id == id)
                ?? throw new CliException($"There is no repository {id} you can use.", ExitCodes.Denied);
        }

        var byUrl = repositories.Where(r => string.Equals(r.Url, idNameOrUrl, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = byUrl.Count > 0
            ? byUrl
            : repositories.Where(r => string.Equals(r.Name, idNameOrUrl, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new CliException($"There is no repository '{idNameOrUrl}' you can use; see 'forest repo list'.", ExitCodes.Denied),
            _ => throw new CliException($"Several repositories are named '{idNameOrUrl}'; use the address or the id.", ExitCodes.Usage),
        };
    }

    /// <summary>A field the API contract always fills; its absence means the server is not what the CLI expects.</summary>
    internal static T Required<T>(T? value, string what)
        where T : class =>
        value ?? throw new CliException($"The server's answer has no {what}; is it a container-forest aggregator of a matching version?");

    internal static T Required<T>(T? value, string what)
        where T : struct =>
        value ?? throw new CliException($"The server's answer has no {what}; is it a container-forest aggregator of a matching version?");
}
