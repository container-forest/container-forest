using ContainerForest.Cli.Api.Generated;
using ContainerForest.Cli.Configuration;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;

namespace ContainerForest.Cli.Api;

/// <summary>Creates the API adapter for a server; tests pass a handler that talks to an aggregator in memory.</summary>
public sealed class ForestApiFactory(Func<HttpMessageHandler>? handlerFactory = null)
{
    /// <summary>Requests that hang longer than this are given up; follow-mode commands poll in short requests.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(17);

    public ForestApi Create(Connection connection) =>
        new(CreateClient(connection.Url, new BearerAuthenticationProvider(connection.Url, connection.Token)), connection.Url);

    /// <summary>Without a token: for starting a login, before there is one.</summary>
    public ForestApi CreateAnonymous(Uri url) => new(CreateClient(url, new AnonymousAuthenticationProvider()), url);

    private ForestApiClient CreateClient(Uri url, IAuthenticationProvider authentication)
    {
        // No retries: a repeated POST could queue a second deployment, and an offline agent stays offline for a while.
        var handlers = KiotaClientFactory.CreateDefaultHandlers()
            .Where(handler => handler is not RetryHandler)
            .Append(new ProblemDetailsHandler())
            .ToArray();
        var finalHandler = handlerFactory?.Invoke() ?? KiotaClientFactory.GetDefaultHttpMessageHandler();
        var httpClient = new HttpClient(KiotaClientFactory.ChainHandlersCollectionAndGetFirstLink(finalHandler, handlers)!);
        // Stack commands wait for compose up to 16 minutes on the aggregator.
        httpClient.Timeout = RequestTimeout;
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"forest/{CliVersion.Current}");

        var adapter = new HttpClientRequestAdapter(authentication, httpClient: httpClient)
        {
            BaseUrl = url.ToString().TrimEnd('/'),
        };
        return new ForestApiClient(adapter);
    }
}
