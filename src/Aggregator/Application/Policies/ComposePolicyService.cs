using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Policies;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Policies;

/// <param name="CanChange">Whether the current user administers the environment and may change the policy.</param>
public sealed record ComposePolicyView(ComposePolicy Policy, bool CanChange);

/// <summary>How a deployed stack fares under the environment's current policy.</summary>
/// <param name="Checked">False when no deployment with the policy on has succeeded yet: nothing is known about it.</param>
public sealed record StackPolicyReport(
    StackId StackId, StackName StackName, bool Checked, IReadOnlyList<PolicyViolation> Violations, IReadOnlyList<string> Warnings);

/// <summary>
/// The compose policy of an environment (ADR-0012). Whoever sees the environment reads it, so Editors know what their
/// compose files may do; administrators of the system or of the environment change it, each change in the audit log.
/// </summary>
public sealed class ComposePolicyService(
    CurrentActor currentActor,
    EnvironmentAdministration administration,
    IEnvironmentRepository environments,
    IRoleAssignmentRepository roleAssignments,
    IStackRepository stacks,
    IComposePolicyRepository policies,
    IUnitOfWork unitOfWork,
    IAuditTrail audit)
{
    /// <exception cref="NotFoundException">The environment does not exist or the user does not see it.</exception>
    public async Task<ComposePolicyView> GetAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var environment = await environments.FindAsync(environmentId, cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);
        if (environment is null || !AccessPolicy.CanSee(actor, environment, assignments))
        {
            throw new NotFoundException("Environment", environmentId);
        }

        return new ComposePolicyView(
            await policies.GetAsync(environmentId, cancellationToken), EnvironmentAdministration.CanAdminister(actor, environment, assignments));
    }

    /// <exception cref="DomainValidationException">A path or an allowance is not valid.</exception>
    public Task<ComposePolicyView> ChangeAsync(
        EnvironmentId environmentId,
        ComposePolicyMode mode,
        string dataRoot,
        IReadOnlyList<string> systemReadOnlyPaths,
        IReadOnlyList<string> allowances,
        CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.ComposePolicyChanged, new EnvironmentAuditTarget(environmentId, environmentId.ToString()));
        return audit.RunAsync(draft, async () =>
        {
            var (_, environment) = await administration.GetAsync(environmentId, cancellationToken);
            draft.Target = new EnvironmentAuditTarget(environment.Id, environment.Name);
            var policy = await policies.FindAsync(environmentId, cancellationToken);
            if (policy is null)
            {
                policy = ComposePolicy.Default(environmentId);
                policies.Add(policy);
            }

            var before = Describe(policy);
            policy.Change(mode, dataRoot, systemReadOnlyPaths, allowances);
            draft.Details = $"{before} → {Describe(policy)}";
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new ComposePolicyView(policy, CanChange: true);
        }, cancellationToken);
    }

    /// <summary>
    /// The environment's visible stacks checked against the current policy with the facts of their deployed version,
    /// as an Enforce deployment would check them now (endorsements aside).
    /// </summary>
    public async Task<IReadOnlyList<StackPolicyReport>> ReportAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var environment = await environments.FindAsync(environmentId, cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);
        if (environment is null || !AccessPolicy.CanSee(actor, environment, assignments))
        {
            throw new NotFoundException("Environment", environmentId);
        }

        var policy = await policies.GetAsync(environmentId, cancellationToken);
        var reports = new List<StackPolicyReport>();
        foreach (var stack in (await stacks.ListByEnvironmentAsync(environmentId, cancellationToken)).OrderBy(s => s.Name.Value, StringComparer.Ordinal))
        {
            if (!AccessPolicy.CanSee(actor, environment, stack, assignments) || stack.IsSystem)
            {
                continue;
            }

            var facts = stack.DeployedComposeFacts is { } json ? ComposeFactsReader.Deserialize(json) : null;
            if (facts is null || stack.DeployedCommit is null)
            {
                reports.Add(new StackPolicyReport(stack.Id, stack.Name, Checked: false, [], []));
                continue;
            }

            var evaluation = ComposePolicyEvaluator.Evaluate(facts, policy, stack.Name.Value, stack.PolicyAllowances);
            reports.Add(new StackPolicyReport(stack.Id, stack.Name, Checked: true, evaluation.Violations, evaluation.Warnings));
        }

        return reports;
    }

    private static string Describe(ComposePolicy policy) =>
        $"{policy.Mode}, data {policy.DataRoot}, {policy.SystemReadOnlyPaths.Count} system path(s), " +
        $"allowances: {(policy.Allowances.Count == 0 ? "none" : string.Join(", ", policy.Allowances))}";
}
