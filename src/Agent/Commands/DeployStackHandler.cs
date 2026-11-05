using System.Text.RegularExpressions;
using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Commands;

/// <summary>
/// Redeploy and redeploy with pull: writes the bundle into the stack directory and runs `docker compose up`.
/// Every line of compose output goes to the aggregator as progress, which shows it in the deployment log. With
/// <see cref="DeployStack.RestartOnFileChange"/>, the stack is restarted afterwards when its other files changed.
/// </summary>
public sealed partial class DeployStackHandler(
    IStackFileStore store,
    IComposeRunner compose,
    RegistryConfigWriter registryConfigs,
    HostPathInspector inspector,
    StackLocks locks,
    IOptions<AgentOptions> options,
    ILogger<DeployStackHandler> logger)
    : StackCommandHandler<DeployStack>(locks, logger)
{
    /// <summary>How long a container has to stop when restarted after a file change; the panel's default for restart.</summary>
    public static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(10);

    protected override string StackNameOf(DeployStack command) => command.StackName;

    protected override async Task RunAsync(
        string stackName, DeployStack command, ICommandProgress progress, CancellationToken cancellationToken)
    {
        // All checks come before the first write, so a rejected bundle leaves the stack as it was.
        if (command.Pull is not (PullPolicy.Missing or PullPolicy.Always))
        {
            throw new InvalidCommandArgumentException("Pull policy must be set to missing or always.");
        }

        if (command.CommitSha.Length > 0 && !CommitPattern().IsMatch(command.CommitSha))
        {
            throw new InvalidCommandArgumentException("Commit SHA must be 7-64 hexadecimal characters.");
        }

        var layout = StackLayout.Create(stackName, command.Files);
        var environment = ValidateEnvironment(command.Env);
        RegistryConfigWriter.Validate(command.RegistryAuths);
        if (command.VerifyPaths.FirstOrDefault(p => !HostPathInspector.IsAcceptable(p)) is { } badPath)
        {
            throw new InvalidCommandArgumentException($"Path to verify '{badPath}' must be absolute and normalized.");
        }

        await progress.ReportAsync(Describe(command, layout), cancellationToken);
        var written = await store.WriteAsync(layout, cancellationToken);

        // As late as possible: a container could plant a link while the stack is being written (ADR-0012).
        foreach (var path in command.VerifyPaths)
        {
            if (inspector.Inspect(path) is { } problem)
            {
                throw new UnsafeMountSourceException($"Compose up was not run: {problem.Describe()}");
            }
        }

        var envFile = command.ExplicitEnvFile
            ? ComposeInvocation.EnvFileFor(written.Directory, RenderStackHandler.HasEnvFile(layout))
            : null;
        var invocation = ComposeInvocation.Up(stackName, written.Directory, command.Pull, environment, envFile);
        using (var registryConfig = await registryConfigs.WriteAsync(command.RegistryAuths, cancellationToken))
        {
            if (registryConfig is not null)
            {
                invocation = invocation.WithEnvironment(RegistryConfigWriter.DockerConfigVariable, registryConfig.Directory);
            }

            await compose.RunAsync(invocation, progress, cancellationToken);
        }

        if (command.RestartOnFileChange)
        {
            await RestartIfFilesChangedAsync(stackName, written.FilesChanged, progress, cancellationToken);
        }

        // The containers now use the new secrets; the old ones should not stay on the host longer than needed.
        store.DiscardPreviousSecrets(stackName);
    }

    // Compose recreated the containers whose definition changed; the rest still have the files read at their start.
    private async Task RestartIfFilesChangedAsync(
        string stackName, bool filesChanged, ICommandProgress progress, CancellationToken cancellationToken)
    {
        if (!filesChanged)
        {
            await progress.ReportAsync("No file other than compose.yaml changed; the containers are not restarted.", cancellationToken);
            return;
        }

        await progress.ReportAsync("Files other than compose.yaml changed; restarting the stack so its containers read them.", cancellationToken);

        // As RestartStackHandler: outside the stack directory and without -f.
        await compose.RunAsync(ComposeInvocation.Restart(stackName, options.Value.StacksRoot, RestartTimeout), progress, cancellationToken);
    }

    /// <summary>Checks the variables for compose; shared with <see cref="RenderStackHandler"/>.</summary>
    internal static Dictionary<string, string> ValidateEnvironment(MapField<string, string> env)
    {
        foreach (var (name, value) in env)
        {
            if (!VariableNamePattern().IsMatch(name))
            {
                throw new InvalidCommandArgumentException("Environment variable names must be letters, digits and '_', not starting with a digit.");
            }

            // Values are secrets, so neither the value nor the name is shown.
            if (value.Contains('\0'))
            {
                throw new InvalidCommandArgumentException("An environment variable value contains a NUL character.");
            }
        }

        return new Dictionary<string, string>(env);
    }

    // Counts only: the file names of secrets are not secret, their content is never shown.
    private static string Describe(DeployStack command, StackLayout layout)
    {
        var commit = command.CommitSha.Length > 0 ? $" at {command.CommitSha[..Math.Min(12, command.CommitSha.Length)]}" : "";
        var secrets = layout.SecretFiles.Count();
        return $"Writing {layout.Files.Count} file(s){commit}" + (secrets > 0 ? $", {secrets} of them secret." : ".");
    }

    [GeneratedRegex("^[0-9a-f]{7,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex VariableNamePattern();
}
