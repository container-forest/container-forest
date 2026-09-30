using System.Text.RegularExpressions;
using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Google.Protobuf.Collections;

namespace ContainerForest.Agent.Commands;

/// <summary>
/// Redeploy and redeploy with pull: writes the bundle into the stack directory and runs `docker compose up`.
/// Every line of compose output goes to the aggregator as progress, which shows it in the deployment log.
/// </summary>
public sealed partial class DeployStackHandler(
    IStackFileStore store,
    IComposeRunner compose,
    RegistryConfigWriter registryConfigs,
    StackLocks locks,
    ILogger<DeployStackHandler> logger)
    : StackCommandHandler<DeployStack>(locks, logger)
{
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

        await progress.ReportAsync(Describe(command, layout), cancellationToken);
        var directory = await store.WriteAsync(layout, cancellationToken);

        var invocation = ComposeInvocation.Up(stackName, directory, command.Pull, environment);
        using (var registryConfig = await registryConfigs.WriteAsync(command.RegistryAuths, cancellationToken))
        {
            if (registryConfig is not null)
            {
                invocation = invocation.WithEnvironment(RegistryConfigWriter.DockerConfigVariable, registryConfig.Directory);
            }

            await compose.RunAsync(invocation, progress, cancellationToken);
        }

        // The containers now use the new secrets; the old ones should not stay on the host longer than needed.
        store.DiscardPreviousSecrets(stackName);
    }

    private static Dictionary<string, string> ValidateEnvironment(MapField<string, string> env)
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
