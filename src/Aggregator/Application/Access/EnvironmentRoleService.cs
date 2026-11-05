using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Access;

/// <param name="StackId">Null for a role on the whole environment.</param>
public sealed record EnvironmentRoleView(
    RoleAssignmentId Id,
    UserId UserId,
    string UserName,
    StackId? StackId,
    string? StackName,
    Role Role,
    DateTimeOffset GrantedAt);

/// <summary>
/// Roles on one environment and its stacks, for the environment's administrators (ADR-0012) as well as administrators.
/// An environment administrator gives existing users roles up to Editor; the environment administrator role itself is
/// given and taken only by administrators, since it states that the person has root on the host. Every change goes to
/// the audit log, as in <see cref="UserManagementService"/>.
/// </summary>
public sealed class EnvironmentRoleService(
    EnvironmentAdministration administration,
    IUserRepository users,
    IRoleAssignmentRepository roleAssignments,
    IStackRepository stacks,
    IUnitOfWork unitOfWork,
    IAuditTrail audit,
    TimeProvider time)
{
    /// <summary>By user name; a role on the whole environment comes before roles on its stacks.</summary>
    public async Task<IReadOnlyList<EnvironmentRoleView>> ListAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var (_, environment) = await administration.GetAsync(environmentId, cancellationToken);
        var views = new List<EnvironmentRoleView>();
        foreach (var assignment in await roleAssignments.ListForEnvironmentAsync(environment.Id, cancellationToken))
        {
            views.Add(await ViewAsync(assignment, cancellationToken));
        }

        return views
            .OrderBy(v => v.UserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.StackName is not null)
            .ThenBy(v => v.StackName, StringComparer.Ordinal)
            .ToList();
    }

    /// <exception cref="NotFoundException">No such user, or the stack is not in the environment.</exception>
    /// <exception cref="DomainConflictException">The user already has a role on this scope.</exception>
    /// <exception cref="AccessDeniedException">An environment administrator tries to give the environment administrator role.</exception>
    public Task<EnvironmentRoleView> GrantAsync(
        EnvironmentId environmentId, string userName, StackId? stackId, Role role, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.RoleGranted, new RoleAssignmentAuditTarget(null, userName, environmentId, stackId))
        {
            Details = $"Role: {role}",
        };
        return audit.RunAsync(draft, async () =>
        {
            var (actor, environment) = await administration.GetAsync(environmentId, cancellationToken);
            EnsureMayGive(actor, role);
            var user = await users.FindByUserNameAsync(userName.Trim(), cancellationToken)
                ?? throw new NotFoundException("User", userName.Trim());
            var existing = await roleAssignments.ListForUserAsync(user.Id, cancellationToken);
            var now = time.GetUtcNow();

            RoleAssignment assignment;
            string scope;
            if (stackId is { } id)
            {
                var stack = await stacks.FindAsync(id, cancellationToken);
                if (stack is null || stack.EnvironmentId != environment.Id)
                {
                    throw new NotFoundException(nameof(Stack), id);
                }

                if (existing.OfType<StackRoleAssignment>().Any(a => a.StackId == id))
                {
                    throw new DomainConflictException($"User '{user.UserName}' already has a role on stack '{stack.Name}'; change it instead.");
                }

                assignment = RoleAssignment.ForStack(user.Id, stack, role, now);
                scope = $"{environment.Name}/{stack.Name}";
            }
            else
            {
                if (existing.OfType<EnvironmentRoleAssignment>().Any(a => a.EnvironmentId == environment.Id))
                {
                    throw new DomainConflictException(
                        $"User '{user.UserName}' already has a role on environment '{environment.Name}'; change it instead.");
                }

                assignment = RoleAssignment.ForEnvironment(user.Id, environment.Id, role, now);
                scope = environment.Name;
            }

            draft.Target = new RoleAssignmentAuditTarget(assignment.Id, user.UserName, environment.Id, stackId);
            draft.Details = $"Role: {role} on {scope}";
            roleAssignments.Add(assignment);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return await ViewAsync(assignment, cancellationToken);
        }, cancellationToken);
    }

    public Task<EnvironmentRoleView> ChangeAsync(
        EnvironmentId environmentId, RoleAssignmentId assignmentId, Role role, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.RoleChanged, new RoleAssignmentAuditTarget(assignmentId, assignmentId.ToString(), environmentId, null))
        {
            Details = $"Role: {role}",
        };
        return audit.RunAsync(draft, async () =>
        {
            var (actor, environment) = await administration.GetAsync(environmentId, cancellationToken);
            var assignment = await GetAssignmentAsync(environment, assignmentId, cancellationToken);
            EnsureMayGive(actor, assignment.Role);
            EnsureMayGive(actor, role);
            var before = await ViewAsync(assignment, cancellationToken);
            draft.Target = new RoleAssignmentAuditTarget(assignment.Id, before.UserName, environment.Id, before.StackId);
            draft.Details = $"Role: {before.Role} → {role} on {ScopeOf(environment, before)}";

            assignment.ChangeRole(role);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return await ViewAsync(assignment, cancellationToken);
        }, cancellationToken);
    }

    public Task RevokeAsync(EnvironmentId environmentId, RoleAssignmentId assignmentId, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.RoleRevoked, new RoleAssignmentAuditTarget(assignmentId, assignmentId.ToString(), environmentId, null));
        return audit.RunAsync(draft, async () =>
        {
            var (actor, environment) = await administration.GetAsync(environmentId, cancellationToken);
            var assignment = await GetAssignmentAsync(environment, assignmentId, cancellationToken);
            EnsureMayGive(actor, assignment.Role);
            var view = await ViewAsync(assignment, cancellationToken);
            draft.Target = new RoleAssignmentAuditTarget(assignment.Id, view.UserName, environment.Id, view.StackId);
            draft.Details = $"Role: {view.Role} on {ScopeOf(environment, view)}";

            roleAssignments.Remove(assignment);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    private static void EnsureMayGive(Actor actor, Role role)
    {
        if (role == Role.EnvironmentAdmin && !actor.IsAdmin)
        {
            throw new AccessDeniedException("Only administrators give or take the environment administrator role.");
        }
    }

    /// <summary>An assignment outside the environment is as unknown as a missing one.</summary>
    private async Task<RoleAssignment> GetAssignmentAsync(
        DockerEnvironment environment, RoleAssignmentId assignmentId, CancellationToken cancellationToken)
    {
        var assignment = await roleAssignments.FindAsync(assignmentId, cancellationToken);
        var inEnvironment = assignment switch
        {
            EnvironmentRoleAssignment e => e.EnvironmentId == environment.Id,
            StackRoleAssignment s => s.EnvironmentId == environment.Id,
            _ => false,
        };
        return inEnvironment ? assignment! : throw new NotFoundException(nameof(RoleAssignment), assignmentId);
    }

    private async Task<EnvironmentRoleView> ViewAsync(RoleAssignment assignment, CancellationToken cancellationToken)
    {
        var user = await users.GetAsync(assignment.UserId, cancellationToken);
        if (assignment is StackRoleAssignment onStack)
        {
            var stack = await stacks.GetAsync(onStack.StackId, cancellationToken);
            return new EnvironmentRoleView(
                assignment.Id, user.Id, user.UserName, stack.Id, stack.Name.Value, assignment.Role, assignment.GrantedAt);
        }

        return new EnvironmentRoleView(assignment.Id, user.Id, user.UserName, null, null, assignment.Role, assignment.GrantedAt);
    }

    private static string ScopeOf(DockerEnvironment environment, EnvironmentRoleView view) =>
        view.StackName is null ? environment.Name : $"{environment.Name}/{view.StackName}";
}
