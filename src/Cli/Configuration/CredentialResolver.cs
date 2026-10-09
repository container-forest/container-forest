namespace ContainerForest.Cli.Configuration;

/// <summary>Where the token of a command came from; <c>forest whoami</c> says it.</summary>
public enum CredentialSource
{
    Option,
    TokenFile,
    EnvironmentVariable,
    Context,
}

/// <summary>The server and the token a command uses.</summary>
/// <param name="ContextName">The context the server or the token came from; null when neither did.</param>
/// <param name="DefaultEnvironment">The context's environment for commands without <c>--env</c>.</param>
public sealed record Connection(Uri Url, string Token, CredentialSource Source, string? ContextName, string? DefaultEnvironment)
{
    // The token stays out of logs and exception messages.
    public override string ToString() => $"{nameof(Connection)} {{ Url = {Url}, Source = {Source}, ContextName = {ContextName} }}";
}

/// <summary>What the command line says about credentials; null where it says nothing.</summary>
public sealed record CredentialOptions(string? Url, string? Token, string? TokenFile, string? Context)
{
    public override string ToString() => $"{nameof(CredentialOptions)} {{ Url = {Url}, Context = {Context} }}";
}

/// <summary>
/// Picks the server and the token of a command, first match wins: <c>--url</c>, <c>CF_URL</c>, the context's address;
/// <c>--token</c>, <c>--token-file</c> (<c>-</c> for stdin), <c>CF_TOKEN</c>, the context's token. The context is
/// <c>--context</c>, <c>CF_CONTEXT</c> or the current one. A context's token is sent only to the context's own server,
/// never to an address given with <c>--url</c> or <c>CF_URL</c>.
/// </summary>
public sealed class CredentialResolver(ConfigStore store, Func<string, string?> getVariable, TextReader standardInput)
{
    public const string UrlVariable = "CF_URL";
    public const string TokenVariable = "CF_TOKEN";
    public const string ContextVariable = "CF_CONTEXT";

    /// <exception cref="CliException">There is no server or no token, or the named context does not exist.</exception>
    public Connection Resolve(CredentialOptions options)
    {
        var config = store.Load();
        var (contextName, context) = FindContext(config, options.Context ?? Variable(ContextVariable));

        var urlText = options.Url ?? Variable(UrlVariable);
        var url = urlText is not null
            ? ServerUrl.Parse(urlText)
            : context is not null
                ? ServerUrl.Parse(context.Url)
                : throw new CliException(
                    "No server: run 'forest login <address>' or set CF_URL and CF_TOKEN.", ExitCodes.Unauthenticated);

        // The context counts for the token and the default environment only when it is about the same server.
        var sameServer = context is not null && ServerUrl.SameServer(ServerUrl.Parse(context.Url), url);
        var (token, source) = ResolveToken(options, sameServer ? context : null)
            ?? throw new CliException(
                $"Not logged in to {url}: run 'forest login {ServerUrl.ContextName(url)}' or set CF_TOKEN.", ExitCodes.Unauthenticated);

        return new Connection(url, token, source, context is not null ? contextName : null, sameServer ? context!.Environment : null);
    }

    /// <summary>The context a command names, or the current one; null when there is none.</summary>
    /// <exception cref="CliException">A context is named explicitly but does not exist.</exception>
    public (string? Name, ContextEntry? Entry) FindContext(CliConfig config, string? requested)
    {
        if (requested is not null)
        {
            return config.Contexts.TryGetValue(requested, out var entry)
                ? (requested, entry)
                : throw new CliException($"There is no context '{requested}'; see 'forest context list'.", ExitCodes.Usage);
        }

        return (config.CurrentContext, config.Current);
    }

    private (string Token, CredentialSource Source)? ResolveToken(CredentialOptions options, ContextEntry? context)
    {
        if (options.Token is { } token)
        {
            return (Clean(token, "--token"), CredentialSource.Option);
        }

        if (options.TokenFile is { } file)
        {
            var text = file == "-" ? standardInput.ReadToEnd() : ReadFile(file);
            return (Clean(text, file == "-" ? "standard input" : file), CredentialSource.TokenFile);
        }

        if (Variable(TokenVariable) is { } fromVariable)
        {
            return (Clean(fromVariable, TokenVariable), CredentialSource.EnvironmentVariable);
        }

        return context?.Token is { } saved ? (saved, CredentialSource.Context) : null;
    }

    private static string ReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"Could not read the token file {path}: {exception.Message}", ExitCodes.Usage);
        }
    }

    private static string Clean(string token, string from)
    {
        var trimmed = token.Trim();
        return trimmed.Length > 0 && !trimmed.Any(char.IsWhiteSpace)
            ? trimmed
            : throw new CliException($"The token from {from} is empty or has spaces in it.", ExitCodes.Usage);
    }

    private string? Variable(string name) => getVariable(name) is { Length: > 0 } value ? value : null;
}
