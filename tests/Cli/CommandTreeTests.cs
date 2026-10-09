using System.CommandLine;
using ContainerForest.Cli.Api;
using ContainerForest.Cli.Commands;
using ContainerForest.Cli.Configuration;
using ContainerForest.Cli.Login;

namespace ContainerForest.Cli.Tests;

public class CommandTreeTests
{
    private static readonly RootCommand Root = ForestCli.Build(new CliHost(
        TextWriter.Null, TextWriter.Null, TextReader.Null, _ => null, new ConfigStore(Path.GetTempPath()), new ForestApiFactory(), new NoBrowser(), TimeProvider.System));

    [Theory]
    [InlineData("stack deploy web --pull")]
    [InlineData("stack down web -e r2 --yes -t 30")]
    [InlineData("container logs web-app-1 -f --tail 50")]
    [InlineData("env registry set ghcr.io -u me")]
    [InlineData("login forest.example.com --device")]
    [InlineData("deployment logs 01a11fb3-4e54-7703-afb3-13230a32e6e9 -f")]
    [InlineData("-o json env list")]
    public void Commands_parse(string commandLine) => Assert.Empty(Root.Parse(commandLine).Errors);

    [Theory]
    [InlineData("stack deploy")]
    [InlineData("stack deploy-on-push web maybe")]
    [InlineData("env registry set ghcr.io")]
    public void Wrong_command_lines_are_refused(string commandLine) => Assert.NotEmpty(Root.Parse(commandLine).Errors);

    [Fact]
    public void Every_command_says_what_it_does()
    {
        var undocumented = All(Root).Where(c => string.IsNullOrWhiteSpace(c.Description)).Select(c => c.Name).ToList();

        Assert.Empty(undocumented);
    }

    private static IEnumerable<Command> All(Command command) => command.Subcommands.SelectMany(All).Prepend(command);

    private sealed class NoBrowser : IBrowser
    {
        public bool TryOpen(Uri page) => false;
    }
}
