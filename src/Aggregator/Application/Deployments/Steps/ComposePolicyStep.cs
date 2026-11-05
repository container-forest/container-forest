using System.Collections.Concurrent;
using System.Text.Json;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Policies;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Policies;

namespace ContainerForest.Aggregator.Application.Deployments.Steps;

/// <summary>
/// Checks the stack against its environment's compose policy (ADR-0012) on the model compose itself would run: the
/// agent renders the bundle with docker compose config, and the aggregator decides. In Audit mode, and for content
/// endorsed by an administrator of the system or of the environment, violations are warnings; in Enforce mode they
/// fail the deployment before compose runs, and the agent checks the mount sources once more right before it does.
/// </summary>
public sealed class ComposePolicyStep(
    IAgentChannel agents,
    IUserRepository users,
    IRoleAssignmentRepository roleAssignments,
    IStackRepository stacks,
    DeploymentOptions options) : IDeployStep
{
    public string Name => "Check compose policy";

    public async Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
    {
        var policy = context.Policy;
        if (policy.IsOff)
        {
            context.Info($"Environment '{context.Environment.Name}' has no compose policy.");
            return;
        }

        // Compose reads only the bundle's .env from now on, as when the stack is rendered.
        context.Bundle = context.Bundle with { ExplicitEnvFile = true };
        var deployment = context.Deployment;
        if (deployment.Trigger == DeploymentTrigger.Rehydrate && deployment.Commit == context.Stack.DeployedCommit)
        {
            // Docker restarts these containers after a reboot anyway; refusing them would only keep their secrets away.
            context.Info($"Commit {deployment.Commit?.Short} is already deployed; the compose policy is not checked again.");
            return;
        }

        var unrestricted = context.Stack.PolicyUnrestricted;
        var facts = await RenderAsync(context, enforce: policy.Mode == ComposePolicyMode.Enforce && !unrestricted, cancellationToken);
        if (facts is null)
        {
            return;
        }

        var stackNames = (await stacks.ListByEnvironmentAsync(context.Environment.Id, cancellationToken)).Select(s => s.Name.Value).ToList();
        var evaluation = ComposePolicyEvaluator.Evaluate(facts, policy, context.Stack.Name.Value, context.Stack.PolicyAllowances, stackNames);
        if (unrestricted)
        {
            // Rendered anyway, so the report and the log say what the stack does on the host.
            context.ComposeFacts = facts;
            foreach (var violation in evaluation.Violations)
            {
                context.Info($"Compose policy (not applied): {violation}.");
            }

            context.Info($"The stack is unrestricted: the compose policy does not limit it ({evaluation.Violations.Count} violation(s) not applied).");
            return;
        }

        foreach (var warning in evaluation.Warnings)
        {
            context.Warn($"Compose policy: {warning}.");
        }

        context.ComposeFacts = facts;
        var exemptBy = evaluation.Passes ? null : await EndorsingAdministratorAsync(context, cancellationToken);
        if (evaluation.Passes || policy.Mode == ComposePolicyMode.Audit || exemptBy is not null)
        {
            foreach (var violation in evaluation.Violations)
            {
                context.Warn($"Compose policy: {violation}.");
            }

            context.Info(evaluation.Passes
                ? $"The stack follows the compose policy of environment '{context.Environment.Name}'."
                : exemptBy is not null
                    ? $"Deploying despite {evaluation.Violations.Count} violation(s): the content is endorsed by {exemptBy}, who administers the environment."
                    : $"Audit mode: {evaluation.Violations.Count} violation(s) would fail the deployment under Enforce.");
            if (policy.Mode == ComposePolicyMode.Enforce && exemptBy is null)
            {
                context.Bundle = context.Bundle with { VerifyPaths = PathsToVerify(facts, policy, context.Stack) };
            }

            return;
        }

        throw new DeploymentFailedException(
            $"The compose policy of environment '{context.Environment.Name}' forbids this stack: " +
            string.Join("; ", evaluation.Violations) +
            ". Administrators of the environment can give the stack allowances, or endorse the content themselves.");
    }

    /// <param name="enforce">An agent too old to render fails the deployment; otherwise it is only a warning.</param>
    private async Task<ComposeFacts?> RenderAsync(DeployContext context, bool enforce, CancellationToken cancellationToken)
    {
        // As in SendToAgentStep: lines arrive on the connection's receive loop and are copied into the log afterwards.
        var output = new ConcurrentQueue<string>();
        RenderedCompose rendered;
        try
        {
            rendered = await agents.RenderStackAsync(context.Environment.Id, context.Bundle, output.Enqueue, options.RenderTimeout, cancellationToken);
        }
        catch (AgentCommandFailedException ex) when (ex.Kind == AgentErrorKind.Unsupported)
        {
            const string why = "the environment's agent is too old to render stacks; update the agent";
            if (enforce)
            {
                throw new DeploymentFailedException($"The compose policy cannot be checked: {why}.");
            }

            context.Warn($"Compose policy not checked: {why}.");
            return null;
        }
        catch (AgentCommandFailedException ex)
        {
            throw new DeploymentFailedException($"Rendering the stack with docker compose config failed: {ex.Message}");
        }
        finally
        {
            while (output.TryDequeue(out var line))
            {
                context.Info(line);
            }
        }

        try
        {
            return ComposeFactsReader.Read(rendered);
        }
        catch (JsonException)
        {
            throw new DeploymentFailedException("The agent's rendering of the stack cannot be read; update the agent.");
        }
    }

    /// <summary>
    /// The name of whoever endorsed the content (ADR-0011) if they administer the system or this environment: people
    /// with root on the host anyway, whom the policy does not stop. Clicking "Deploy" is not an endorsement.
    /// </summary>
    private async Task<string?> EndorsingAdministratorAsync(DeployContext context, CancellationToken cancellationToken)
    {
        var deployment = context.Deployment;
        var endorser = deployment.Endorsement switch
        {
            CommitEndorsement.Signed => deployment.SignedBy,
            CommitEndorsement.ApprovedUnsigned => deployment.RequestedBy,
            _ => null,
        };
        if (endorser is not { } id || await users.FindAsync(id, cancellationToken) is not { IsActive: true } user)
        {
            return null;
        }

        var assignments = await roleAssignments.ListForUserAsync(user.Id, cancellationToken);
        return EnvironmentAdministration.CanAdminister(user, context.Environment, assignments) ? user.UserName : null;
    }

    /// <summary>
    /// The host paths mounted where a container could have left a symbolic link (<see cref="ComposePolicyEvaluator.IsContainerWritable"/>):
    /// links elsewhere, such as /var/run → /run, belong to the host.
    /// </summary>
    private static IReadOnlyList<string> PathsToVerify(ComposeFacts facts, ComposePolicy policy, Domain.Stacks.Stack stack) =>
        facts.Services
            .SelectMany(s => s.Mounts)
            .Where(m => m.Type == "bind" && m.Source is not null)
            .Select(m => m.Source!)
            .Concat(facts.Files)
            .Select(HostPath.TryNormalize)
            .OfType<string>()
            .Where(p => !policy.SystemReadOnlyPaths.Any(system => HostPath.IsAtOrUnder(p, system)))
            .Where(p => ComposePolicyEvaluator.IsContainerWritable(p, facts, policy, stack.Name.Value, stack.PolicyAllowances))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
}
