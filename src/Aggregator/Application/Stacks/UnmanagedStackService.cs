using System.Collections.Concurrent;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Registries;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Stacks;

/// <param name="CanRestart">The user may restart it (Operator on the environment); for hiding buttons only.</param>
/// <param name="CanDeploy">The user may pull, bring it up and take it down (Editor on the environment); for hiding buttons only.</param>
public sealed record UnmanagedStackView(UnmanagedStackSnapshot Stack, bool CanRestart, bool CanDeploy);

/// <summary>
/// Compose projects the host's administrator runs by hand (ADR-0016). They have no stack record, so every
/// permission is the user's role on the environment as a whole, as for containers outside managed stacks: restart
/// needs Operator, the rest Editor, as for managed stacks. A name that belongs to a managed stack is never unmanaged.
/// </summary>
public sealed class UnmanagedStackService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IRoleAssignmentRepository roleAssignments,
    IRegistryCredentialRepository registryCredentials,
    ISecretProtector protector,
    IAgentChannel agents,
    IAuditTrail audit)
{
    /// <summary>Unmanaged stacks of the environment; empty for a user without a role on the environment as a whole.</summary>
    public async Task<IReadOnlyList<UnmanagedStackView>> ListAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var (actor, environment, assignments) = await LoadAsync(environmentId, cancellationToken);
        if (!AccessPolicy.CanPerform(actor, environment, assignments, Permission.View))
        {
            return [];
        }

        var managed = await ManagedNamesAsync(environment, cancellationToken);
        var canRestart = AccessPolicy.CanPerform(actor, environment, assignments, Permission.ControlContainers);
        var canDeploy = AccessPolicy.CanPerform(actor, environment, assignments, Permission.Deploy);
        return (await agents.ListUnmanagedStacksAsync(environment.Id, cancellationToken))
            .Where(s => !managed.Contains(s.Name))
            .Select(s => new UnmanagedStackView(s, canRestart && !s.IsSystem, canDeploy && !s.IsSystem))
            .ToList();
    }

    /// <param name="stopTimeout">For restart and down, per container; <see cref="StackCommand.DefaultStopTimeout"/> when null.</param>
    /// <returns>The compose output; a failed compose run is a result with an error, as for managed stacks.</returns>
    public async Task<StackCommandResult> ExecuteAsync(
        EnvironmentId environmentId, string stackName, UnmanagedStackAction action, TimeSpan? stopTimeout, CancellationToken cancellationToken)
    {
        var timeout = ValidateTimeout(stopTimeout ?? StackCommand.DefaultStopTimeout);
        var (actor, environment, assignments) = await LoadAsync(environmentId, cancellationToken);
        var name = stackName.Trim();

        // A user who does not see the environment's own containers does not see its unmanaged stacks either.
        if (!AccessPolicy.CanPerform(actor, environment, assignments, Permission.View) || !StackName.TryParse(name, out var parsed))
        {
            throw new NotFoundException("Unmanaged stack", $"'{name}'");
        }

        var draft = new AuditDraft(AuditActionFor(action), new StackAuditTarget(null, parsed.Value, environment.Id));
        return await audit.RunAsync(draft, async () =>
        {
            // The agent's list is the only source; a managed stack of the same name is never run as an unmanaged one.
            var stack = (await ManagedNamesAsync(environment, cancellationToken)).Contains(parsed.Value)
                ? null
                : (await agents.ListUnmanagedStacksAsync(environment.Id, cancellationToken)).FirstOrDefault(s => s.Name == parsed.Value);
            if (stack is null)
            {
                throw new NotFoundException("Unmanaged stack", $"'{parsed}'");
            }

            // After the lookup: whoever sees the environment's containers may see which unmanaged stacks exist.
            var permission = action == UnmanagedStackAction.Restart ? Permission.ControlContainers : Permission.Deploy;
            if (!AccessPolicy.CanPerform(actor, environment, assignments, permission))
            {
                var role = RolePermissions.MinimumRole(permission);
                throw new AccessDeniedException(
                    $"Only the {role} role or higher on the whole environment may {Verb(action)} unmanaged stack '{parsed}'.");
            }

            // The aggregator, the panel or the agent itself: stopping or recreating them would cut the system off the host.
            if (stack.IsSystem)
            {
                throw new DomainConflictException(
                    $"Cannot {Verb(action)} unmanaged stack '{parsed}': it runs a part of container-forest itself.");
            }

            var logins = action is UnmanagedStackAction.Up or UnmanagedStackAction.Pull
                ? await LoginsAsync(environment, cancellationToken)
                : [];

            var output = new ConcurrentQueue<string>();
            StackCommandResult result;
            try
            {
                // Not the caller's token, as for managed stacks: a closed tab must not stop compose halfway.
                await agents.ControlUnmanagedStackAsync(
                    environment.Id, parsed, action, timeout, logins, output.Enqueue, CancellationToken.None);
                result = new StackCommandResult(null, Collect(output));
            }
            catch (AgentCommandFailedException failed) when (failed.Kind is AgentErrorKind.CommandFailed or AgentErrorKind.Timeout)
            {
                result = new StackCommandResult(failed.Message, Collect(output));
                draft.Fail(failed.Message);
            }

            return result;
        }, cancellationToken);
    }

    private async Task<(Actor Actor, DockerEnvironment Environment, IReadOnlyList<RoleAssignment> Assignments)> LoadAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var environment = await environments.FindAsync(environmentId, cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);

        // Checked before contacting the agent, and reported as missing so its existence does not leak.
        if (environment is null || !AccessPolicy.CanSee(actor, environment, assignments))
        {
            throw new NotFoundException("Environment", environmentId);
        }

        return (actor, environment, assignments);
    }

    private async Task<HashSet<string>> ManagedNamesAsync(DockerEnvironment environment, CancellationToken cancellationToken) =>
        (await stacks.ListByEnvironmentAsync(environment.Id, cancellationToken)).Select(s => s.Name.Value).ToHashSet(StringComparer.Ordinal);

    // The aggregator does not read the stack's compose files, so it cannot tell which registries its images need:
    // the agent gets all of the environment's logins, as for a managed stack whose image names are interpolated.
    private async Task<IReadOnlyList<RegistryLogin>> LoginsAsync(DockerEnvironment environment, CancellationToken cancellationToken)
    {
        var logins = new List<RegistryLogin>();
        foreach (var credential in await registryCredentials.ListByEnvironmentAsync(environment.Id, cancellationToken))
        {
            try
            {
                logins.Add(new RegistryLogin(
                    RegistryHost.ConfigKey(credential.Server),
                    credential.Username,
                    protector.Unprotect(SecretPurpose.RegistryPassword, credential.ProtectedPassword)));
            }
            catch (SecretUnreadableException)
            {
                throw new DomainConflictException(
                    $"The stored password for registry '{credential.Server}' cannot be decrypted; set the credentials for this environment again.");
            }
        }

        return logins;
    }

    private static IReadOnlyList<string> Collect(ConcurrentQueue<string> output) =>
        output.Count > StackCommand.MaxOutputLines ? output.Skip(output.Count - StackCommand.MaxOutputLines).ToList() : output.ToList();

    private static TimeSpan ValidateTimeout(TimeSpan timeout) =>
        timeout >= TimeSpan.Zero && timeout <= StackCommand.MaxStopTimeout
            ? timeout
            : throw new DomainValidationException($"Stop timeout must be between 0 and {StackCommand.MaxStopTimeout.TotalSeconds:0} seconds.");

    private static string Verb(UnmanagedStackAction action) => action switch
    {
        UnmanagedStackAction.Up => "bring up",
        UnmanagedStackAction.Pull => "pull",
        UnmanagedStackAction.Restart => "restart",
        _ => "take down",
    };

    private static AuditAction AuditActionFor(UnmanagedStackAction action) => action switch
    {
        UnmanagedStackAction.Up => AuditAction.UnmanagedStackStarted,
        UnmanagedStackAction.Pull => AuditAction.UnmanagedStackPulled,
        UnmanagedStackAction.Restart => AuditAction.UnmanagedStackRestarted,
        UnmanagedStackAction.Down => AuditAction.UnmanagedStackTakenDown,
        _ => throw new DomainValidationException($"Unknown unmanaged stack action {action}."),
    };
}
