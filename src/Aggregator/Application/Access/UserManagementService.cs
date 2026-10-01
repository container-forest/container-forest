using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
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
/// Every method is for administrators only.
/// </summary>
public sealed class UserManagementService(
    CurrentActor currentActor,
    IUserRepository users,
    IRoleAssignmentRepository roleAssignments,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IAccountStore accounts,
    IUnitOfWork unitOfWork,
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
    public async Task<UserSummary> CreateAsync(string userName, string password, bool isAdmin, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var now = time.GetUtcNow();
        var user = isAdmin ? User.CreateAdministrator(userName, now) : User.Create(userName, now);
        if (await users.FindByUserNameAsync(user.UserName, cancellationToken) is { } existing)
        {
            throw new DomainConflictException($"User '{existing.UserName}' already exists.");
        }

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
    public async Task<RoleAssignmentView> GrantRoleAsync(
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

    public async Task<RoleAssignmentView> ChangeRoleAsync(
        UserId userId, RoleAssignmentId assignmentId, Role role, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var assignment = await GetAssignmentAsync(userId, assignmentId, cancellationToken);
        assignment.ChangeRole(role);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await ViewAsync(assignment, cancellationToken);
    }

    public async Task RevokeRoleAsync(UserId userId, RoleAssignmentId assignmentId, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        roleAssignments.Remove(await GetAssignmentAsync(userId, assignmentId, cancellationToken));
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<UserSummary> RunAsync(UserId id, UserAccountAction action, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var user = await users.GetAsync(id, cancellationToken);
        await action.ExecuteAsync(user, new UserActionContext(users, accounts), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await SummarizeAsync(user, cancellationToken);
    }

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
