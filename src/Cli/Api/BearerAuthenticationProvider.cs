using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;

namespace ContainerForest.Cli.Api;

/// <summary>
/// Adds <c>Authorization: Bearer</c> with the API token, only to requests for the server the token belongs to:
/// an address the CLI did not expect never gets the token.
/// </summary>
internal sealed class BearerAuthenticationProvider(Uri server, string token) : IAuthenticationProvider
{
    public Task AuthenticateRequestAsync(
        RequestInformation request,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        if (Uri.Compare(request.URI, server, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
        {
            request.Headers.Add("Authorization", $"Bearer {token}");
        }

        return Task.CompletedTask;
    }
}
