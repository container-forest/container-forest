using System.CommandLine;
using ContainerForest.Cli.Api;
using ContainerForest.Cli.Configuration;
using ContainerForest.Cli.Login;
using ContainerForest.Cli.Output;
using Microsoft.Kiota.Abstractions;

namespace ContainerForest.Cli.Commands;

/// <summary>
/// <c>forest login [server]</c>: gets an API token through the browser (or <c>--device</c> by code, or
/// <c>--with-token</c> from standard input), checks it and saves it in a context, which becomes the current one.
/// </summary>
internal sealed class LoginCommand() : ForestCommand("login", "Sign in to an aggregator and save the token in a context.")
{
    private readonly Argument<string?> server = new("server")
    {
        Description = "Aggregator address, e.g. forest.example.com; defaults to the current context's.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private readonly Option<bool> device = new("--device")
    {
        Description = "Do not open a browser here: show a code to enter in the panel on any device (e.g. over SSH).",
    };

    private readonly Option<bool> noBrowser = new("--no-browser") { Description = "Print the approval page instead of opening it." };

    private readonly Option<bool> withToken = new("--with-token")
    {
        Description = "Read an existing API token from standard input instead of signing in through the browser.",
    };

    private readonly Option<string?> name = new("--name") { Description = "Context name; defaults to the server's host." };

    public override bool NeedsConnection => false;

    protected override void Configure(Command command)
    {
        command.Add(server);
        command.Add(device);
        command.Add(noBrowser);
        command.Add(withToken);
        command.Add(name);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var host = context.Host;
        var config = host.ConfigStore.Load();
        var url = ServerUrl.Parse(ServerAddress(context, config));
        var contextName = context.Value(name)
            ?? config.Contexts.FirstOrDefault(c => ServerUrl.SameServer(ServerUrl.Parse(c.Value.Url), url)).Key
            ?? ServerUrl.ContextName(url);

        string token;
        string userName;
        if (context.Value(withToken))
        {
            token = (await host.Input.ReadToEndAsync(context.CancellationToken)).Trim();
            if (token.Length == 0)
            {
                throw new CliException("No token on standard input.", ExitCodes.Usage);
            }

            var me = await host.ApiFactory.Create(new Connection(url, token, CredentialSource.TokenFile, null, null))
                .GetMeAsync(context.CancellationToken);
            userName = me.UserName ?? "?";
        }
        else
        {
            var login = new BrowserLogin(host.ApiFactory.CreateAnonymous(url), host.Browser, host.Time, context.Output);
            var issued = await login.RunAsync(context.Value(device), !context.Value(noBrowser), context.CancellationToken);
            token = issued.Token ?? throw new CliException("The server approved the login but sent no token.");
            userName = issued.UserName ?? "?";
        }

        var previous = config.Contexts.GetValueOrDefault(contextName);
        await RevokeReplacedAsync(context, previous, url, token);

        config.Contexts[contextName] = new ContextEntry
        {
            Url = url.ToString(),
            Token = token,
            UserName = userName,
            Environment = previous is not null && ServerUrl.SameServer(ServerUrl.Parse(previous.Url), url) ? previous.Environment : null,
        };
        config.CurrentContext = contextName;
        host.ConfigStore.Save(config);

        context.Output.Note($"Logged in to {url} as {userName}; the token is saved in context '{contextName}' ({host.ConfigStore.FilePath}).");
        return ExitCodes.Ok;
    }

    private string ServerAddress(CommandContext context, CliConfig config)
    {
        if ((context.Value(server) ?? context.Value(GlobalOptions.Url) ?? context.Host.GetVariable(CredentialResolver.UrlVariable)) is { Length: > 0 } given)
        {
            return given;
        }

        var (_, entry) = context.Host.Credentials.FindContext(config, context.Value(GlobalOptions.Context));
        return entry?.Url ?? throw new CliException("Which server? Run 'forest login <address>'.", ExitCodes.Usage);
    }

    // A second login to the same server would otherwise leave the old token valid and forgotten.
    private static async Task RevokeReplacedAsync(CommandContext context, ContextEntry? previous, Uri url, string newToken)
    {
        if (previous?.Token is not { } old || old == newToken || !ServerUrl.SameServer(ServerUrl.Parse(previous.Url), url))
        {
            return;
        }

        try
        {
            await context.Host.ApiFactory.Create(new Connection(url, old, CredentialSource.Context, null, null))
                .RevokeCurrentTokenAsync(context.CancellationToken);
        }
        catch (Exception exception) when (exception is ServerProblemException or ApiException or HttpRequestException)
        {
            // Already revoked or expired, or unreachable for a moment: the new token works either way.
        }
    }
}

/// <summary><c>forest logout</c>: revokes the token on the server and forgets it.</summary>
internal sealed class LogoutCommand() : ForestCommand("logout", "Revoke the token on the server and remove it from the context.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var connection = context.Connection;
        try
        {
            await context.Api.RevokeCurrentTokenAsync(context.CancellationToken);
        }
        catch (ServerProblemException exception) when (exception.Status == 401)
        {
            context.Output.Note("The server no longer accepted the token; forgetting it anyway.");
        }

        if (connection is { Source: CredentialSource.Context, ContextName: { } name })
        {
            var store = context.Host.ConfigStore;
            var config = store.Load();
            if (config.Contexts.TryGetValue(name, out var entry))
            {
                entry.Token = null;
                entry.UserName = null;
                store.Save(config);
            }
        }
        else
        {
            context.Output.Note("The token was revoked; it came from the command line or the environment, so nothing was removed from the configuration.");
        }

        context.Output.Note($"Logged out of {connection.Url}.");
        return ExitCodes.Ok;
    }
}

/// <summary><c>forest whoami</c>: who the token acts as, on which server, and where the token came from.</summary>
internal sealed class WhoAmICommand() : ForestCommand("whoami", "Show the user and server the CLI acts as.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var me = await context.Api.GetMeAsync(context.CancellationToken);
        var connection = context.Connection;
        await context.Output.ItemAsync(me, m =>
        [
            ("User", m.UserName),
            ("Administrator", CliOutput.YesNo(m.IsAdmin)),
            ("Server", connection.Url.ToString()),
            ("Context", connection.ContextName),
            ("Token from", connection.Source switch
            {
                CredentialSource.Option => "--token",
                CredentialSource.TokenFile => "--token-file",
                CredentialSource.EnvironmentVariable => CredentialResolver.TokenVariable,
                _ => "the context",
            }),
            ("Default environment", connection.DefaultEnvironment),
        ]);
        return ExitCodes.Ok;
    }
}
