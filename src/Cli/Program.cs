using ContainerForest.Cli.Commands;

namespace ContainerForest.Cli;

internal static class Program
{
    private static Task<int> Main(string[] args) => ForestCli.Build(CliHost.FromProcess()).Parse(args).InvokeAsync();
}
