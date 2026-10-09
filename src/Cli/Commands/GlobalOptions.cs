using System.CommandLine;
using ContainerForest.Cli.Configuration;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

/// <summary>Options every command takes: which server, which credentials, which output.</summary>
internal static class GlobalOptions
{
    public static readonly Option<string?> Url = new("--url")
    {
        Description = "Aggregator address; overrides CF_URL and the context.",
        Recursive = true,
    };

    public static readonly Option<string?> Token = new("--token")
    {
        Description = "API token; visible to other users in the process list, prefer --token-file or CF_TOKEN.",
        Recursive = true,
    };

    public static readonly Option<string?> TokenFile = new("--token-file")
    {
        Description = "Read the API token from a file, or from standard input with '-'.",
        Recursive = true,
    };

    public static readonly Option<string?> Context = new("--context")
    {
        Description = "Use this context instead of the current one (also CF_CONTEXT).",
        Recursive = true,
    };

    public static readonly Option<OutputFormat> Output = new("--output", "-o")
    {
        Description = "table for people, json for scripts.",
        HelpName = "table|json",
        DefaultValueFactory = _ => OutputFormat.Table,
        Recursive = true,
    };

    public static IEnumerable<Option> All => [Url, Token, TokenFile, Context, Output];

    public static CredentialOptions CredentialsFrom(ParseResult parseResult) => new(
        parseResult.GetValue(Url),
        parseResult.GetValue(Token),
        parseResult.GetValue(TokenFile),
        parseResult.GetValue(Context));
}
