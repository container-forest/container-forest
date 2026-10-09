using ContainerForest.Cli.Commands;

namespace ContainerForest.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var parseResult = ForestCli.Build(CliHost.FromProcess()).Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // System.CommandLine reports a wrong command line with 1, which forest keeps for failed operations.
        return parseResult.Errors.Count > 0 ? ExitCodes.Usage : exitCode;
    }
}
