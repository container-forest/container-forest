using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Access;

public class UserManagementServiceTests
{
    private const string Password = "a long enough password";

    private readonly InMemoryUsers users = new();
    private readonly FakeAuditTrail audit = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly InMemoryStacks stacks = new();
    private readonly FakeAccountStore accounts = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly UserManagementService service;

    private readonly User admin = User.CreateAdministrator("root", Now);
    private readonly RecordingNotifier notifier = new();
    private readonly User alice = User.Create("alice", Now);
    private readonly DockerEnvironment prod = DockerEnvironment.Create("prod", isPrivate: true, Now);
    private readonly DockerEnvironment staging = DockerEnvironment.Create("staging", isPrivate: false, Now);
    private readonly Stack web;

    public UserManagementServiceTests()
    {
        users.Add(admin);
        users.Add(alice);
        environments.Add(prod);
        environments.Add(staging);
        web = Stack.Create(
            prod.Id, StackName.Parse("web"), RepositoryId.New(), BranchName.Parse("main"),
            StackFilePath.Parse("compose.yaml"), isSystem: false, Now);
        stacks.Add(web);
        currentUser.Id = admin.Id;
        service = new UserManagementService(
            new CurrentActor(currentUser, users), users, roleAssignments, environments, stacks, accounts, unitOfWork, audit, notifier, new FixedTime(Now));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_administrators_manage_users()
    {
        currentUser.Id = alice.Id;

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.CreateAsync("bob", Password, isAdmin: false, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.DeactivateAsync(admin.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.GrantRoleAsync(alice.Id, prod.Id, null, Role.Editor, Ct));
    }

    [Fact]
    public async Task List_shows_users_by_name_with_their_lockouts()
    {
        accounts.Lockouts[alice.Id] = Now.AddMinutes(10);
        accounts.Lockouts[admin.Id] = Now.AddMinutes(-1);

        var list = await service.ListAsync(Ct);

        Assert.Equal(["alice", "root"], list.Select(u => u.User.UserName));
        Assert.Equal(Now.AddMinutes(10), list[0].LockedOutUntil);
        Assert.Null(list[1].LockedOutUntil);
    }

    [Fact]
    public async Task Create_stores_the_user_with_an_account()
    {
        var created = await service.CreateAsync("bob", Password, isAdmin: true, Ct);

        Assert.True(created.User.IsAdmin);
        Assert.Same(created.User, await users.FindByUserNameAsync("bob", Ct));
        Assert.Equal(Password, accounts.Passwords[created.User.Id]);
    }

    [Fact]
    public async Task Create_rejects_a_taken_name_regardless_of_case()
    {
        await Assert.ThrowsAsync<DomainConflictException>(() => service.CreateAsync("ALICE", Password, isAdmin: false, Ct));
    }

    [Fact]
    public async Task Rejected_password_leaves_no_user_behind()
    {
        await Assert.ThrowsAsync<PasswordRejectedException>(() => service.CreateAsync("bob", "short", isAdmin: false, Ct));

        Assert.Null(await users.FindByUserNameAsync("bob", Ct));
    }

    [Fact]
    public async Task The_last_active_administrator_cannot_be_deactivated_or_lose_the_flag()
    {
        await Assert.ThrowsAsync<DomainConflictException>(() => service.DeactivateAsync(admin.Id, Ct));
        await Assert.ThrowsAsync<DomainConflictException>(() => service.SetAdminAsync(admin.Id, isAdmin: false, Ct));

        Assert.True(admin.IsActive);
        Assert.True(admin.IsAdmin);
    }

    [Fact]
    public async Task An_administrator_can_go_while_another_active_one_stays()
    {
        await service.SetAdminAsync(alice.Id, isAdmin: true, Ct);

        await service.DeactivateAsync(admin.Id, Ct);

        Assert.False(admin.IsActive);
    }

    [Fact]
    public async Task A_deactivated_administrator_does_not_count_as_the_one_that_stays()
    {
        var bob = User.CreateAdministrator("bob", Now);
        bob.Deactivate();
        users.Add(bob);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.DeactivateAsync(admin.Id, Ct));
    }

    [Fact]
    public async Task Taking_access_away_ends_the_users_sessions()
    {
        await service.SetAdminAsync(alice.Id, isAdmin: true, Ct);
        await service.SetAdminAsync(alice.Id, isAdmin: false, Ct);
        await service.DeactivateAsync(alice.Id, Ct);
        await service.ResetPasswordAsync(alice.Id, "a brand new passphrase", Ct);

        Assert.Equal([alice.Id, alice.Id, alice.Id], accounts.EndedSessions);
        Assert.Equal("a brand new passphrase", accounts.Passwords[alice.Id]);
    }

    [Fact]
    public async Task Granting_and_activating_do_not_end_sessions()
    {
        await service.SetAdminAsync(alice.Id, isAdmin: true, Ct);
        await service.ActivateAsync(alice.Id, Ct);

        Assert.Empty(accounts.EndedSessions);
    }

    [Fact]
    public async Task A_deactivated_user_cannot_become_an_administrator()
    {
        await service.DeactivateAsync(alice.Id, Ct);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.SetAdminAsync(alice.Id, isAdmin: true, Ct));
    }

    [Fact]
    public async Task Rename_renames_the_account_too()
    {
        var renamed = await service.RenameAsync(alice.Id, "alice.smith", Ct);

        Assert.Equal("alice.smith", renamed.User.UserName);
        Assert.Equal("alice.smith", accounts.UserNames[alice.Id]);
    }

    [Fact]
    public async Task Rename_to_a_taken_name_is_a_conflict_but_changing_the_case_is_fine()
    {
        await Assert.ThrowsAsync<DomainConflictException>(() => service.RenameAsync(alice.Id, "Root", Ct));

        var renamed = await service.RenameAsync(alice.Id, "Alice", Ct);

        Assert.Equal("Alice", renamed.User.UserName);
    }

    [Fact]
    public async Task Unlock_lifts_the_lockout()
    {
        accounts.Lockouts[alice.Id] = Now.AddMinutes(10);

        var unlocked = await service.UnlockAsync(alice.Id, Ct);

        Assert.Null(unlocked.LockedOutUntil);
    }

    [Fact]
    public async Task Unknown_user_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => service.DeactivateAsync(UserId.New(), Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(UserId.New(), Ct));
    }

    [Fact]
    public async Task Roles_on_environments_and_stacks_are_listed_by_environment_then_stack()
    {
        await service.GrantRoleAsync(alice.Id, staging.Id, null, Role.Viewer, Ct);
        await service.GrantRoleAsync(alice.Id, prod.Id, web.Id, Role.Editor, Ct);
        await service.GrantRoleAsync(alice.Id, prod.Id, null, Role.Operator, Ct);

        var details = await service.GetAsync(alice.Id, Ct);

        Assert.Equal(
            [("prod", null, Role.Operator), ("prod", "web", Role.Editor), ("staging", null, Role.Viewer)],
            details.RoleAssignments.Select(a => (a.EnvironmentName, a.StackName, a.Role)));
    }

    [Fact]
    public async Task One_role_per_scope()
    {
        await service.GrantRoleAsync(alice.Id, prod.Id, null, Role.Viewer, Ct);
        await service.GrantRoleAsync(alice.Id, prod.Id, web.Id, Role.Viewer, Ct);

        await Assert.ThrowsAsync<DomainConflictException>(() => service.GrantRoleAsync(alice.Id, prod.Id, null, Role.Editor, Ct));
        await Assert.ThrowsAsync<DomainConflictException>(() => service.GrantRoleAsync(alice.Id, prod.Id, web.Id, Role.Editor, Ct));
    }

    [Fact]
    public async Task A_stack_of_another_environment_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => service.GrantRoleAsync(alice.Id, staging.Id, web.Id, Role.Viewer, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GrantRoleAsync(alice.Id, EnvironmentId.New(), null, Role.Viewer, Ct));
    }

    [Fact]
    public async Task Change_and_revoke_a_role()
    {
        var granted = await service.GrantRoleAsync(alice.Id, prod.Id, null, Role.Viewer, Ct);

        var changed = await service.ChangeRoleAsync(alice.Id, granted.Id, Role.Editor, Ct);
        await service.RevokeRoleAsync(alice.Id, granted.Id, Ct);

        Assert.Equal(Role.Editor, changed.Role);
        Assert.Empty((await service.GetAsync(alice.Id, Ct)).RoleAssignments);
    }

    [Fact]
    public async Task Another_users_assignment_is_not_found()
    {
        var granted = await service.GrantRoleAsync(alice.Id, prod.Id, null, Role.Viewer, Ct);

        await Assert.ThrowsAsync<NotFoundException>(() => service.ChangeRoleAsync(admin.Id, granted.Id, Role.Editor, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.RevokeRoleAsync(admin.Id, granted.Id, Ct));
    }

    [Fact]
    public async Task Changes_are_recorded_under_their_own_action()
    {
        currentUser.Id = admin.Id;
        var bob = await service.CreateAsync("bob", Password, isAdmin: false, Ct);
        await service.RenameAsync(bob.User.Id, "robert", Ct);
        await service.DeactivateAsync(bob.User.Id, Ct);
        await Assert.ThrowsAsync<DomainConflictException>(() => service.DeactivateAsync(admin.Id, Ct));

        Assert.Equal(
            [
                (AuditAction.UserCreated, AuditOutcome.Succeeded, "bob", null),
                (AuditAction.UserRenamed, AuditOutcome.Succeeded, "bob", "New name: robert"),
                (AuditAction.UserDeactivated, AuditOutcome.Succeeded, "robert", null),
                (AuditAction.UserDeactivated, AuditOutcome.Failed, "root", "'root' is the last active administrator: nobody else could manage users."),
            ],
            audit.Entries.Select(e => (e.Action, e.Outcome, e.Target?.Name, e.Details)));
    }

    [Fact]
    public async Task A_refused_change_is_recorded_as_denied()
    {
        currentUser.Id = alice.Id;

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.DeactivateAsync(admin.Id, Ct));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal((AuditAction.UserDeactivated, AuditOutcome.Denied), (entry.Action, entry.Outcome));
    }

    [Fact]
    public async Task A_granted_role_is_recorded_with_its_scope()
    {
        currentUser.Id = admin.Id;

        await service.GrantRoleAsync(alice.Id, prod.Id, null, Role.Operator, Ct);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal((AuditAction.RoleGranted, "alice", "Role: Operator on prod"), (entry.Action, entry.Target?.Name, entry.Details));
        Assert.Equal(prod.Id, entry.Target?.EnvironmentId);
    }

    [Fact]
    public async Task Granting_administrator_rights_is_announced_with_who_did_it()
    {
        await service.SetAdminAsync(alice.Id, true, Ct);

        var (kind, summary) = Assert.Single(notifier.Sent);
        Assert.Equal(NotificationEventKinds.AccountSecurityChanged, kind);
        Assert.Contains("'alice'", summary);
        Assert.Contains(admin.UserName, summary);
    }

    [Fact]
    public async Task Granting_to_an_administrator_changes_nothing_and_announces_nothing()
    {
        await service.SetAdminAsync(admin.Id, true, Ct);

        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task A_password_reset_is_announced_without_the_password()
    {
        await service.ResetPasswordAsync(alice.Id, Password, Ct);

        var (kind, summary) = Assert.Single(notifier.Sent);
        Assert.Equal(NotificationEventKinds.AccountSecurityChanged, kind);
        Assert.Contains("'alice'", summary);
        Assert.DoesNotContain(Password, summary);
    }

    [Fact]
    public async Task Creating_an_administrator_is_announced_and_an_ordinary_user_is_not()
    {
        await service.CreateAsync("bob", Password, isAdmin: false, Ct);
        Assert.Empty(notifier.Sent);

        await service.CreateAsync("carol", Password, isAdmin: true, Ct);

        Assert.Contains("'carol'", Assert.Single(notifier.Sent).Summary);
    }

    [Fact]
    public async Task Other_changes_announce_nothing()
    {
        await service.RenameAsync(alice.Id, "alicia", Ct);
        await service.SetAdminAsync(alice.Id, false, Ct);

        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task A_refused_reset_announces_nothing()
    {
        currentUser.Id = alice.Id;

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ResetPasswordAsync(admin.Id, Password, Ct));

        Assert.Empty(notifier.Sent);
    }

    private sealed class RecordingNotifier : ISystemNotifier
    {
        public List<(NotificationEventKinds Kind, string Summary)> Sent { get; } = [];

        public Task NotifyAsync(NotificationEventKinds kind, string summary)
        {
            Sent.Add((kind, summary));
            return Task.CompletedTask;
        }
    }
}
