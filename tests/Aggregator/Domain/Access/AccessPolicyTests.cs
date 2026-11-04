using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Access;

public class AccessPolicyTests
{
    [Fact]
    public void Effective_role_is_the_highest_of_environment_and_stack_roles()
    {
        var user = User();
        var environment = Environment(isPrivate: true);
        var stack = Stack(environment.Id);
        RoleAssignment[] assignments =
        [
            RoleAssignment.ForEnvironment(user.Id, environment.Id, Role.Viewer, Now),
            RoleAssignment.ForStack(user.Id, stack, Role.Operator, Now),
        ];

        Assert.Equal(Role.Operator, AccessPolicy.EffectiveRole(user, environment, stack, assignments));
        Assert.Equal(Role.Viewer, AccessPolicy.EffectiveRole(user, environment, assignments));
    }

    [Fact]
    public void Stack_role_does_not_apply_to_other_stacks()
    {
        var user = User();
        var environment = Environment(isPrivate: true);
        var web = Stack(environment.Id, "web");
        var db = Stack(environment.Id, "db");
        RoleAssignment[] assignments = [RoleAssignment.ForStack(user.Id, web, Role.Editor, Now)];

        Assert.True(AccessPolicy.CanPerform(user, environment, web, assignments, Permission.Deploy));
        Assert.False(AccessPolicy.CanSee(user, environment, db, assignments));
        Assert.False(AccessPolicy.CanPerform(user, environment, assignments, Permission.View));
    }

    [Fact]
    public void Private_environment_is_visible_only_with_a_role_on_it_or_its_stack()
    {
        var user = User();
        var environment = Environment(isPrivate: true);
        var stack = Stack(environment.Id);

        Assert.False(AccessPolicy.CanSee(user, environment, []));
        Assert.True(AccessPolicy.CanSee(user, environment, [RoleAssignment.ForStack(user.Id, stack, Role.Viewer, Now)]));
    }

    [Fact]
    public void Public_environment_gives_an_implicit_viewer_role()
    {
        var user = User();
        var environment = Environment(isPrivate: false);
        var stack = Stack(environment.Id);

        Assert.True(AccessPolicy.CanSee(user, environment, []));
        Assert.True(AccessPolicy.CanPerform(user, environment, stack, [], Permission.ViewLogs));
        Assert.False(AccessPolicy.CanPerform(user, environment, stack, [], Permission.ControlContainers));
    }

    [Fact]
    public void Roles_of_other_users_are_ignored()
    {
        var user = User();
        var environment = Environment(isPrivate: true);
        RoleAssignment[] assignments = [RoleAssignment.ForEnvironment(UserId.New(), environment.Id, Role.Editor, Now)];

        Assert.False(AccessPolicy.CanSee(user, environment, assignments));
        Assert.Null(AccessPolicy.EffectiveRole(user, environment, assignments));
    }

    [Fact]
    public void Admin_can_do_everything_until_deactivated()
    {
        var admin = Admin();
        var environment = Environment(isPrivate: true);
        var stack = Stack(environment.Id);

        Assert.True(AccessPolicy.CanPerform(admin, environment, stack, [], Permission.Deploy));

        admin.Deactivate();

        Assert.False(AccessPolicy.CanSee(admin, environment, []));
        Assert.False(AccessPolicy.CanPerform(admin, environment, stack, [], Permission.View));
    }

    [Theory]
    [InlineData(Role.Viewer, StackOperation.Restart, false)]
    [InlineData(Role.Operator, StackOperation.Restart, true)]
    [InlineData(Role.Operator, StackOperation.Redeploy, false)]
    [InlineData(Role.Operator, StackOperation.Down, false)]
    [InlineData(Role.Editor, StackOperation.RedeployWithPull, true)]
    [InlineData(Role.Editor, StackOperation.Remove, true)]
    public void Stack_operations_require_the_matching_role(Role role, StackOperation operation, bool allowed)
    {
        var user = User();
        var environment = Environment(isPrivate: true);
        var stack = Stack(environment.Id);
        RoleAssignment[] assignments = [RoleAssignment.ForEnvironment(user.Id, environment.Id, role, Now)];

        Assert.Equal(allowed, AccessPolicy.CanPerform(user, environment, stack, assignments, operation));
    }

    [Fact]
    public void Rejects_a_stack_from_another_environment()
    {
        var user = User();
        var environment = Environment();
        var foreignStack = Stack(EnvironmentId.New());

        Assert.Throws<ArgumentException>(() => AccessPolicy.EffectiveRole(user, environment, foreignStack, []));
    }

    [Fact]
    public void Environment_administrator_administers_only_their_environment_and_deploys_its_stacks()
    {
        var user = User();
        var environment = Environment(isPrivate: true);
        var other = Environment(isPrivate: false);
        var stack = Stack(environment.Id);
        RoleAssignment[] assignments = [RoleAssignment.ForEnvironment(user.Id, environment.Id, Role.EnvironmentAdmin, Now)];

        Assert.True(AccessPolicy.CanPerform(user, environment, assignments, Permission.AdministerEnvironment));
        Assert.True(AccessPolicy.CanPerform(user, environment, stack, assignments, Permission.Deploy));
        Assert.False(AccessPolicy.CanPerform(user, other, assignments, Permission.AdministerEnvironment));
        Assert.Contains(environment.Id, AccessPolicy.EditorEnvironments(user, assignments));
    }

    [Fact]
    public void Editor_and_restricted_administrator_token_do_not_administer_an_environment()
    {
        var editor = User();
        var environment = Environment(isPrivate: false);
        RoleAssignment[] assignments = [RoleAssignment.ForEnvironment(editor.Id, environment.Id, Role.Editor, Now)];
        var restrictedAdmin = new Actor(Domain.Access.User.CreateAdministrator("root", Now), new AccessScope([environment.Id], readOnly: false));

        Assert.False(AccessPolicy.CanPerform(editor, environment, assignments, Permission.AdministerEnvironment));
        Assert.False(AccessPolicy.CanPerform(restrictedAdmin, environment, [], Permission.AdministerEnvironment));
    }

    [Fact]
    public void Environment_administrator_role_cannot_be_given_on_a_stack()
    {
        var user = User();
        var stack = Stack(EnvironmentId.New());

        Assert.Throws<DomainValidationException>(() => RoleAssignment.ForStack(user.Id, stack, Role.EnvironmentAdmin, Now));
        var onStack = RoleAssignment.ForStack(user.Id, stack, Role.Editor, Now);
        Assert.Throws<DomainValidationException>(() => onStack.ChangeRole(Role.EnvironmentAdmin));
    }
}
