using System.Net;

namespace ContainerForest.Cli.Configuration;

/// <summary>The aggregator's address as the user types it: "forest.example.com", "https://forest.example.com/".</summary>
public static class ServerUrl
{
    /// <summary>An absolute https address without path, query or credentials; http only for this machine.</summary>
    /// <exception cref="CliException">The address is not usable.</exception>
    public static Uri Parse(string value)
    {
        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") || url.Host.Length == 0)
        {
            throw new CliException($"'{value}' is not a server address; use e.g. https://forest.example.com.", ExitCodes.Usage);
        }

        if (url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
        {
            throw new CliException("The server address must not contain credentials, a query or a fragment.", ExitCodes.Usage);
        }

        // The token goes in a header: over plain http anyone on the way would read it.
        if (url.Scheme == "http" && !IsLoopback(url))
        {
            throw new CliException($"{url.Host} is not this machine, so the token would travel in clear text; use https.", ExitCodes.Usage);
        }

        return new Uri(url.GetLeftPart(UriPartial.Authority) + url.AbsolutePath.TrimEnd('/') + "/");
    }

    /// <summary>Whether two addresses are the same server, so a token saved for one may be sent to the other.</summary>
    public static bool SameServer(Uri first, Uri second) =>
        Uri.Compare(first, second, UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>A context name for a server: its host, e.g. "forest.example.com".</summary>
    public static string ContextName(Uri url) => url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}";

    private static bool IsLoopback(Uri url) =>
        url.IsLoopback || (IPAddress.TryParse(url.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
