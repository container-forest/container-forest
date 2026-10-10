using System.CommandLine;
using ContainerForest.Cli.Api;
using ContainerForest.Cli.Configuration;
using ContainerForest.Cli.Login;
using ContainerForest.Cli.Output;
using Microsoft.Kiota.Abstractions;

namespace ContainerForest.Cli.Commands;

/// <summary>
/// What the CLI needs from the outside world, so tests can replace it: the terminal, environment variables, the
/// configuration directory, the network and the browser.
/// </summary>
public sealed class CliHost(
    TextWriter output,
    TextWriter error,
    TextReader input,
    Func<string, string?> getVariable,
    ConfigStore configStore,
    ForestApiFactory apiFactory,
    IBrowser browser,
    TimeProvider time,
    bool interactiveTerminal = false,
    Stream? standardInput = null,
    Stream? standardOutput = null,
    string? workingDirectory = null)
{
    /// <summary>Standard input as bytes, for the git filter's binary protocol.</summary>
    public Stream StandardInput => standardInput ?? Console.OpenStandardInput();

    /// <summary>Standard output as bytes, for the git filter's binary protocol and decrypted files.</summary>
    public Stream StandardOutput => standardOutput ?? Console.OpenStandardOutput();

    /// <summary>Where git commands of the secrets commands run.</summary>
    public string WorkingDirectory => workingDirectory ?? Directory.GetCurrentDirectory();

    /// <summary>Standard input is a person at a terminal, not a pipe: secrets are then typed without echo.</summary>
    public bool InteractiveTerminal { get; } = interactiveTerminal;

    public TextReader Input { get; } = input;

    public Func<string, string?> GetVariable { get; } = getVariable;

    public ConfigStore ConfigStore { get; } = configStore;

    public ForestApiFactory ApiFactory { get; } = apiFactory;

    public IBrowser Browser { get; } = browser;

    public TimeProvider Time { get; } = time;

    public CredentialResolver Credentials => new(ConfigStore, GetVariable, Input);

    public static CliHost FromProcess() => new(
        Console.Out,
        Console.Error,
        Console.In,
        Environment.GetEnvironmentVariable,
        ConfigStore.Default(Environment.GetEnvironmentVariable),
        new ForestApiFactory(),
        new SystemBrowser(),
        TimeProvider.System,
        interactiveTerminal: !Console.IsInputRedirected);

    /// <summary>
    /// Runs a command: resolves the credentials it needs, then turns every expected failure into a message on
    /// standard error and an exit code (<see cref="ExitCodes"/>).
    /// </summary>
    internal async Task<int> RunAsync(ForestCommand command, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var cliOutput = new CliOutput(output, error, parseResult.GetValue(GlobalOptions.Output));
        Connection? connection = null;
        try
        {
            connection = command.NeedsConnection ? Credentials.Resolve(GlobalOptions.CredentialsFrom(parseResult)) : null;
            var api = connection is null ? null : ApiFactory.Create(connection);
            return await command.InvokeAsync(new CommandContext(this, parseResult, cliOutput, connection, api, cancellationToken));
        }
        catch (CliException exception)
        {
            await error.WriteLineAsync($"forest: {exception.Message}");
            return exception.ExitCode;
        }
        catch (ServerProblemException exception)
        {
            var translated = ApiErrors.Translate(exception, connection?.Url ?? new Uri("https://server/"));
            await error.WriteLineAsync($"forest: {translated.Message}");
            return translated.ExitCode;
        }
        catch (ApiException exception)
        {
            var translated = ApiErrors.Translate(exception, connection?.Url ?? new Uri("https://server/"));
            await error.WriteLineAsync($"forest: {translated.Message}");
            return translated.ExitCode;
        }
        catch (HttpRequestException exception)
        {
            await error.WriteLineAsync($"forest: {ApiErrors.Unreachable(exception, connection?.Url ?? new Uri("https://server/")).Message}");
            return ExitCodes.Unreachable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Interrupted;
        }
    }
}
