using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Access;

/// <param name="LockedOutUntil">Set while the account is locked after too many failed sign-ins.</param>
public sealed record UserSummary(User User, DateTimeOffset? LockedOutUntil);

/// <param name="StackId">Null for a role on the whole environment.</param>
public sealed record RoleAssignmentView(
    RoleAssignmentId Id,
    EnvironmentId EnvironmentId,
    string EnvironmentName,
    StackId? StackId,
    string? StackName,
    Role Role,
    DateTimeOffset GrantedAt);

/// <param name="RoleAssignments">By environment name, then stack name; a role on the whole environment comes first.</param>
public sealed record UserDetails(UserSummary User, IReadOnlyList<RoleAssignmentView> RoleAssignments);

/// <summary>
/// User management for administrators: accounts, the admin flag, passwords and roles on environments and stacks.
/// Every method is for administrators only. Every change is recorded in the audit log, refused and failed ones too.
/// </summary>
public sealed class UserManagementService(
    CurrentActor currentActor,
    IUserRepository users,
    IRoleAssignmentRepository roleAssignments,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IAccountStore accounts,
    IUnitOfWork unitOfWork,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var lockouts = await accounts.ListLockoutsAsync(time.GetUtcNow(), cancellationToken);
        return (await users.ListAsync(cancellationToken))
            .Select(user => new UserSummary(user, lockouts.TryGetValue(user.Id, out var until) ? until : null))
            .ToList();
    }

    public async Task<UserDetails> GetAsync(UserId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var user = await users.GetAsync(id, cancellationToken);

        var views = new List<RoleAssignmentView>();
        foreach (var assignment in await roleAssignments.ListForUserAsync(id, cancellationToken))
        {
            views.Add(await ViewAsync(assignment, cancellationToken));
        }

        var ordered = views
            .OrderBy(v => v.EnvironmentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.StackName, StringComparer.Ordinal)
            .ToList();
        return new UserDetails(await SummarizeAsync(user, cancellationToken), ordered);
    }

    /// <param name="password">Initial password; the administrator hands it over.</param>
    public Task<UserSummary> CreateAsync(string userName, string password, bool isAdmin, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.UserCreated, new UserAuditTarget(null, userName.Trim()))
        {
            Details = isAdmin ? "Administrator" : null,
        };
        return audit.RunAsync(draft, () => CreateUserAsync(draft, userName, password, isAdmin, cancellationToken), cancellationToken);
    }

    private async Task<UserSummary> CreateUserAsync(
        AuditDraft draft, string userName, string password, bool isAdmin, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var now = time.GetUtcNow();
        var user = isAdmin ? User.CreateAdministrator(userName, now) : User.Create(userName, now);
        if (await users.FindByUserNameAsync(user.UserName, cancellationToken) is { } existing)
        {
            throw new DomainConflictException($"User '{existing.UserName}' already exists.");
        }

        draft.Target = new UserAuditTarget(user.Id, user.UserName);
        users.Add(user);
        try
        {
            await accounts.CreateAsync(user, password, cancellationToken);
        }
        catch (PasswordRejectedException)
        {
            users.Remove(user);
            throw;
        }

        return new UserSummary(user, LockedOutUntil: null);
    }

    public Task<UserSummary> RenameAsync(UserId id, string userName, CancellationToken cancellationToken) =>
        RunAsync(id, new RenameUserAction(userName), cancellationToken);

    public Task<UserSummary> DeactivateAsync(UserId id, CancellationToken cancellationToken) =>
        RunAsync(id, new DeactivateUserAction(), cancellationToken);

    public Task<UserSummary> ActivateAsync(UserId id, CancellationToken cancellationToken) =>
        RunAsync(id, new ActivateUserAction(), cancellationToken);

    public Task<UserSummary> SetAdminAsync(UserId id, bool isAdmin, CancellationToken cancellationToken) =>
        RunAsync(id, isAdmin ? new GrantAdminAction() : new RevokeAdminAction(), cancellationToken);

    public Task<UserSummary> ResetPasswordAsync(UserId id, string newPassword, CancellationToken cancellationToken) =>
        RunAsync(id, new ResetPasswordAction(newPassword), cancellationToken);

    public Task<UserSummary> UnlockAsync(UserId id, CancellationToken cancellationToken) =>
        RunAsync(id, new UnlockUserAction(), cancellationToken);

    /// <summary>
    /// Grants a role on an environment (<paramref name="stackId"/> null) or on one of its stacks. A user has at
    /// most one role per scope: a second one is a conflict, change the existing one instead.
    /// </summary>
    public Task<RoleAssignmentView> GrantRoleAsync(
        UserId userId, EnvironmentId environmentId, StackId? stackId, Role role, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.RoleGranted, new RoleAssignmentAuditTarget(null, userId.ToString(), environmentId, stackId))
        {
            Details = $"Role: {role}",
        };
        return audit.RunAsync(draft, async () =>
        {
            var view = await GrantRoleCoreAsync(userId, environmentId, stackId, role, cancellationToken);
            draft.Target = TargetOf(view, await NameOfAsync(userId, cancellationToken));
            draft.Details = $"Role: {role} on {ScopeOf(view)}";
            return view;
        }, cancellationToken);
    }

    private async Task<RoleAssignmentView> GrantRoleCoreAsync(
        UserId userId, EnvironmentId environmentId, StackId? stackId, Role role, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var user = await users.GetAsync(userId, cancellationToken);
        var environment = await environments.GetAsync(environmentId, cancellationToken);
        var existing = await roleAssignments.ListForUserAsync(user.Id, cancellationToken);
        var now = time.GetUtcNow();

        RoleAssignment assignment;
        if (stackId is { } id)
        {
            var stack = await stacks.FindAsync(id, cancellationToken);
            if (stack is null || stack.EnvironmentId != environment.Id)
            {
                throw new NotFoundException(nameof(Stack), id);
            }

            if (existing.OfType<StackRoleAssignment>().Any(a => a.StackId == id))
            {
                throw new DomainConflictException(
                    $"User '{user.UserName}' already has a role on stack '{stack.Name}'; change it instead.");
            }

            assignment = RoleAssignment.ForStack(user.Id, stack, role, now);
        }
        else
        {
            if (existing.OfType<EnvironmentRoleAssignment>().Any(a => a.EnvironmentId == environment.Id))
            {
                throw new DomainConflictException(
                    $"User '{user.UserName}' already has a role on environment '{environment.Name}'; change it instead.");
            }

            assignment = RoleAssignment.ForEnvironment(user.Id, environment.Id, role, now);
        }

        roleAssignments.Add(assignment);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(assignment, cancellationToken);
    }

    public Task<RoleAssignmentView> ChangeRoleAsync(
        UserId userId, RoleAssignmentId assignmentId, Role role, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.RoleChanged, new UserAuditTarget(userId, userId.ToString())) { Details = $"Role: {role}" };
        return audit.RunAsync(draft, async () =>
        {
            await currentActor.GetAdministratorAsync(cancellationToken);
            var assignment = await GetAssignmentAsync(userId, assignmentId, cancellationToken);
            var before = await ViewAsync(assignment, cancellationToken);
            draft.Target = TargetOf(before, await NameOfAsync(userId, cancellationToken));
            draft.Details = $"Role: {before.Role} → {role} on {ScopeOf(before)}";

            assignment.ChangeRole(role);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return await ViewAsync(assignment, cancellationToken);
        }, cancellationToken);
    }

    public Task RevokeRoleAsync(UserId userId, RoleAssignmentId assignmentId, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.RoleRevoked, new UserAuditTarget(userId, userId.ToString()));
        return audit.RunAsync(draft, async () =>
        {
            await currentActor.GetAdministratorAsync(cancellationToken);
            var assignment = await GetAssignmentAsync(userId, assignmentId, cancellationToken);
            var view = await ViewAsync(assignment, cancellationToken);
            draft.Target = TargetOf(view, await NameOfAsync(userId, cancellationToken));
            draft.Details = $"Role: {view.Role} on {ScopeOf(view)}";

            roleAssignments.Remove(assignment);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Runs one change of a user, recorded under the action's own <see cref="UserAccountAction.AuditAction"/>.</summary>
    private Task<UserSummary> RunAsync(UserId id, UserAccountAction action, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(action.AuditAction, new UserAuditTarget(id, id.ToString())) { Details = action.AuditDetails };
        return audit.RunAsync(draft, async () =>
        {
            await currentActor.GetAdministratorAsync(cancellationToken);
            var user = await users.GetAsync(id, cancellationToken);
            draft.Target = new UserAuditTarget(user.Id, user.UserName);
            await action.ExecuteAsync(user, new UserActionContext(users, accounts), cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return await SummarizeAsync(user, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>The user's name for the audit log; their id when they do not exist.</summary>
    private async Task<string> NameOfAsync(UserId id, CancellationToken cancellationToken) =>
        (await users.FindAsync(id, cancellationToken))?.UserName ?? id.ToString();

    private static RoleAssignmentAuditTarget TargetOf(RoleAssignmentView view, string userName) =>
        new(view.Id, userName, view.EnvironmentId, view.StackId);

    private static string ScopeOf(RoleAssignmentView view) =>
        view.StackName is null ? view.EnvironmentName : $"{view.EnvironmentName}/{view.StackName}";

    private async Task<UserSummary> SummarizeAsync(User user, CancellationToken cancellationToken)
    {
        var lockouts = await accounts.ListLockoutsAsync(time.GetUtcNow(), cancellationToken);
        return new UserSummary(user, lockouts.TryGetValue(user.Id, out var until) ? until : null);
    }

    /// <summary>An assignment of another user is as unknown as a missing one.</summary>
    private async Task<RoleAssignment> GetAssignmentAsync(
        UserId userId, RoleAssignmentId assignmentId, CancellationToken cancellationToken)
    {
        var assignment = await roleAssignments.FindAsync(assignmentId, cancellationToken);
        return assignment is not null && assignment.UserId == userId
            ? assignment
            : throw new NotFoundException(nameof(RoleAssignment), assignmentId);
    }

    private async Task<RoleAssignmentView> ViewAsync(RoleAssignment assignment, CancellationToken cancellationToken)
    {
        switch (assignment)
        {
            case StackRoleAssignment onStack:
                var stack = await stacks.GetAsync(onStack.StackId, cancellationToken);
                var stackEnvironment = await environments.GetAsync(onStack.EnvironmentId, cancellationToken);
                return new RoleAssignmentView(
                    assignment.Id, stackEnvironment.Id, stackEnvironment.Name, stack.Id, stack.Name.Value, assignment.Role, assignment.GrantedAt);
            case EnvironmentRoleAssignment onEnvironment:
                var environment = await environments.GetAsync(onEnvironment.EnvironmentId, cancellationToken);
                return new RoleAssignmentView(
                    assignment.Id, environment.Id, environment.Name, null, null, assignment.Role, assignment.GrantedAt);
            default:
                throw new InvalidOperationException($"Unknown role assignment {assignment.GetType().Name}.");
        }
    }
}
