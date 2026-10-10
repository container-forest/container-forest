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
    [InlineData("user role grant alice -e r2 --role Operator")]
    [InlineData("connection add team --prefix https://gitea.example.com/team/ --env r2 --env l2 --with-token")]
    [InlineData("notification rule add ops --event DeploymentFailed --event AgentOffline -e r2 --stack web")]
    [InlineData("repo update app --public")]
    [InlineData("login forest.example.com --secrets")]
    [InlineData("secrets init --rule stacks/prod/=prod --rule stacks/staging/=staging --recovery age1x")]
    [InlineData("secrets init -e prod -e staging --pattern '*.secret.yaml'")]
    [InlineData("secrets add app/.env secrets/db")]
    [InlineData("secrets check --staged")]
    [InlineData("secrets updatekeys")]
    [InlineData("git setup --local-sops")]
    [InlineData("git textconv /tmp/x_.env")]
    public void Commands_parse(string commandLine) => Assert.Empty(Root.Parse(commandLine).Errors);

    [Theory]
    [InlineData("stack deploy")]
    [InlineData("stack deploy-on-push web maybe")]
    [InlineData("env registry set ghcr.io")]
    [InlineData("user role grant alice -e r2 --role Root")]
    [InlineData("user set-admin alice maybe")]
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
