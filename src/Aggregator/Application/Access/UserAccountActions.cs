using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Access;

/// <summary>What an administrator's action on a user may use.</summary>
public sealed record UserActionContext(IUserRepository Users, IAccountStore Accounts);

/// <summary>
/// An administrator's change to one user: checks that the change is allowed, applies it, and ends the user's
/// sessions when the change takes access away. The caller checks that the actor is an administrator and saves.
/// </summary>
public abstract class UserAccountAction
{
    /// <summary>How the change appears in the audit log.</summary>
    public abstract AuditAction AuditAction { get; }

    /// <summary>What else the audit log should say about the change, e.g. the new name. Never a password.</summary>
    public virtual string? AuditDetails => null;

    /// <summary>Whether the user's existing sessions must end, so the change applies to them right away.</summary>
    protected virtual bool EndsSessions => false;

    public async Task ExecuteAsync(User user, UserActionContext context, CancellationToken cancellationToken)
    {
        await CheckAsync(user, context, cancellationToken);
        await ApplyAsync(user, context, cancellationToken);
        if (EndsSessions)
        {
            await context.Accounts.EndSessionsAsync(user.Id, cancellationToken);
        }
    }

    /// <exception cref="DomainConflictException">The change is not allowed in the current state.</exception>
    protected virtual Task CheckAsync(User user, UserActionContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    protected abstract Task ApplyAsync(User user, UserActionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Base for changes that can take away an active administrator. The last one must stay: without an active
/// administrator nobody could manage users any more.
/// </summary>
public abstract class AdministratorRemovingAction : UserAccountAction
{
    protected override bool EndsSessions => true;

    protected override async Task CheckAsync(User user, UserActionContext context, CancellationToken cancellationToken)
    {
        if (user is { IsAdmin: true, IsActive: true } && await context.Users.CountActiveAdministratorsAsync(cancellationToken) <= 1)
        {
            throw new DomainConflictException(
                $"'{user.UserName}' is the last active administrator: nobody else could manage users.");
        }
    }
}

public sealed class DeactivateUserAction : AdministratorRemovingAction
{
    public override AuditAction AuditAction => AuditAction.UserDeactivated;

    protected override Task ApplyAsync(User user, UserActionContext context, CancellationToken cancellationToken)
    {
        user.Deactivate();
        return Task.CompletedTask;
    }
}

public sealed class RevokeAdminAction : AdministratorRemovingAction
{
    public override AuditAction AuditAction => AuditAction.AdminRevoked;

    protected override Task ApplyAsync(User user, UserActionContext context, CancellationToken cancellationToken)
    {
        user.RevokeAdmin();
        return Task.CompletedTask;
    }
}

public sealed class ActivateUserAction : UserAccountAction
{
    public override AuditAction AuditAction => AuditAction.UserActivated;

    protected override Task ApplyAsync(User user, UserActionContext context, CancellationToken cancellationToken)
    {
        user.Activate();
        return Task.CompletedTask;
    }
}

/// <summary>Only an active user can become an administrator (see <see cref="User.GrantAdmin"/>).</summary>
public sealed class GrantAdminAction : UserAccountAction
{
    public override AuditAction AuditAction => AuditAction.AdminGranted;

    protected override Task ApplyAsync(User user, UserActionContext context, CancellationToken cancellationToken)
    {
        user.GrantAdmin();
        return Task.CompletedTask;
    }
}

/// <summary>User names are unique regardless of case; changing only the case of one's own name is fine.</summary>
public sealed class RenameUserAction(string userName) : UserAccountAction
{
    public override AuditAction AuditAction => AuditAction.UserRenamed;

    public override string? AuditDetails => $"New name: {userName.Trim()}";

    protected override async Task CheckAsync(User user, UserActionContext context, CancellationToken cancellationToken)
    {
        var existing = await context.Users.FindByUserNameAsync(userName.Trim(), cancellationToken);
        if (existing is not null && existing.Id != user.Id)
        {
            throw new DomainConflictException($"User '{existing.UserName}' already exists.");
        }
    }

    protected override Task ApplyAsync(User user, UserActionContext context, CancellationToken cancellationToken)
    {
        user.Rename(userName);
        return context.Accounts.RenameAsync(user, cancellationToken);
    }
}

/// <summary>Sets a password the administrator hands over; the account store ends the user's sessions with it.</summary>
public sealed class ResetPasswordAction(string newPassword) : UserAccountAction
{
    public override AuditAction AuditAction => AuditAction.PasswordReset;

    protected override Task ApplyAsync(User user, UserActionContext context, CancellationToken cancellationToken) =>
        context.Accounts.SetPasswordAsync(user, newPassword, cancellationToken);
}

public sealed class UnlockUserAction : UserAccountAction
{
    public override AuditAction AuditAction => AuditAction.UserUnlocked;

    protected override Task ApplyAsync(User user, UserActionContext context, CancellationToken cancellationToken) =>
        context.Accounts.UnlockAsync(user.Id, cancellationToken);
}
