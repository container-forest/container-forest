using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Access;

/// <summary>How the limits of an API token narrow <see cref="AccessPolicy"/>.</summary>
public class AccessScopeTests
{
    [Fact]
    public void Full_scope_changes_nothing()
    {
        var user = User();
        var environment = Environment(isPrivate: false);
        var stack = Stack(environment.Id);
        var actor = new Actor(user, AccessScope.Full);

        Assert.True(AccessPolicy.CanSee(actor, environment, []));
        Assert.Equal(Role.Viewer, AccessPolicy.EffectiveRole(actor, environment, stack, []));
    }

    [Fact]
    public void Restricted_scope_hides_other_environments_even_public_ones()
    {
        var user = User();
        var inside = Environment(isPrivate: false);
        var outside = Environment(isPrivate: false);
        var actor = new Actor(user, new AccessScope([inside.Id], readOnly: false));

        Assert.True(AccessPolicy.CanSee(actor, inside, []));
        Assert.False(AccessPolicy.CanSee(actor, outside, []));
        Assert.Null(AccessPolicy.EffectiveRole(actor, outside, []));
    }

    [Fact]
    public void Restricted_scope_keeps_the_users_own_role_inside()
    {
        var user = User();
        var environment = Environment(isPrivate: true);
        var stack = Stack(environment.Id);
        RoleAssignment[] assignments = [RoleAssignment.ForEnvironment(user.Id, environment.Id, Role.Operator, Now)];
        var actor = new Actor(user, new AccessScope([environment.Id], readOnly: false));

        Assert.True(AccessPolicy.CanPerform(actor, environment, stack, assignments, Permission.ControlContainers));
        Assert.False(AccessPolicy.CanPerform(actor, environment, stack, assignments, Permission.Deploy));
    }

    [Fact]
    public void Restricted_scope_with_no_environments_left_reaches_nothing()
    {
        var actor = new Actor(Admin(), new AccessScope([], readOnly: false));

        Assert.False(AccessPolicy.CanSee(actor, Environment(isPrivate: false), []));
    }

    [Fact]
    public void Administrators_restricted_token_is_an_editor_inside_and_no_administrator()
    {
        var admin = Admin();
        var inside = Environment(isPrivate: true);
        var outside = Environment(isPrivate: true);
        var stack = Stack(inside.Id);
        var actor = new Actor(admin, new AccessScope([inside.Id], readOnly: false));

        Assert.False(actor.IsAdmin);
        Assert.True(AccessPolicy.CanSee(actor, inside, []));
        Assert.Equal(Role.Editor, AccessPolicy.EffectiveRole(actor, inside, stack, []));
        Assert.True(AccessPolicy.CanPerform(actor, inside, stack, [], StackOperation.Redeploy));
        Assert.False(AccessPolicy.CanSee(actor, outside, []));
        Assert.Equal([inside.Id], AccessPolicy.EditorEnvironments(actor, []));
    }

    [Fact]
    public void Administrators_unrestricted_token_stays_an_administrator()
    {
        var actor = new Actor(Admin(), new AccessScope(null, readOnly: false));

        Assert.True(actor.IsAdmin);
        Assert.True(AccessPolicy.CanSee(actor, Environment(isPrivate: true), []));
    }

    [Fact]
    public void Read_only_scope_allows_viewing_only()
    {
        var admin = Admin();
        var environment = Environment(isPrivate: true);
        var stack = Stack(environment.Id);
        var actor = new Actor(admin, new AccessScope(null, readOnly: true));

        Assert.True(AccessPolicy.CanSee(actor, environment, stack, []));
        Assert.True(AccessPolicy.CanPerform(actor, environment, stack, [], Permission.ViewLogs));
        Assert.False(AccessPolicy.CanPerform(actor, environment, stack, [], Permission.ControlContainers));
        Assert.False(AccessPolicy.CanPerform(actor, environment, [], Permission.Deploy));
    }

    [Fact]
    public void Editor_environments_are_narrowed_to_the_scope()
    {
        var user = User();
        var first = Environment(isPrivate: true);
        var second = Environment(isPrivate: true);
        RoleAssignment[] assignments =
        [
            RoleAssignment.ForEnvironment(user.Id, first.Id, Role.Editor, Now),
            RoleAssignment.ForEnvironment(user.Id, second.Id, Role.Editor, Now),
        ];
        var actor = new Actor(user, new AccessScope([second.Id], readOnly: false));

        Assert.Equal([second.Id], AccessPolicy.EditorEnvironments(actor, assignments));
    }
}
