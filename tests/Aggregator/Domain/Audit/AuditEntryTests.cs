using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Audit;

public class AuditEntryTests
{
    private static readonly AuditActor Alice = new(UserId.New(), "alice", AuditChannel.Panel, null, "203.0.113.7");

    [Fact]
    public void Entry_copies_the_actor_and_derives_the_category()
    {
        var entry = AuditEntry.Record(Alice, AuditAction.RoleGranted, AuditOutcome.Succeeded, null, "Role: Operator", Now);

        Assert.Equal(AuditCategory.Users, entry.Category);
        Assert.Equal((Alice.UserId, "alice", AuditChannel.Panel, "203.0.113.7"), (entry.ActorId, entry.ActorName, entry.Channel, entry.Address));
        Assert.Equal(Now, entry.OccurredAt);
    }

    [Fact]
    public void Every_action_has_a_category()
    {
        foreach (var action in Enum.GetValues<AuditAction>())
        {
            Assert.True(Enum.IsDefined(AuditActions.CategoryOf(action)));
        }
    }

    [Fact]
    public void Stack_target_puts_the_entry_in_its_environment_and_stack()
    {
        var environment = Environment();
        var stack = Stack(environment.Id);

        var entry = AuditEntry.Record(
            Alice, AuditAction.StackRestarted, AuditOutcome.Succeeded, new StackAuditTarget(stack.Id, "web", environment.Id), null, Now);

        Assert.Equal((AuditTargetKind.Stack, stack.Id.ToString(), "web"), (entry.TargetKind, entry.TargetId, entry.TargetName));
        Assert.Equal((environment.Id, stack.Id), (entry.EnvironmentId, entry.StackId));
    }

    [Fact]
    public void Container_target_is_named_and_scoped_without_a_stack()
    {
        var environment = EnvironmentId.New();

        var entry = AuditEntry.Record(
            Alice, AuditAction.ContainerStopped, AuditOutcome.Denied, new ContainerAuditTarget("db-1", environment, null), null, Now);

        Assert.Equal((AuditTargetKind.Container, "db-1"), (entry.TargetKind, entry.TargetName));
        Assert.Equal(environment, entry.EnvironmentId);
        Assert.Null(entry.StackId);
    }

    [Fact]
    public void Details_are_one_line_and_cut_to_length()
    {
        var entry = AuditEntry.Record(
            Alice, AuditAction.DeploymentQueued, AuditOutcome.Failed, null, "first\r\nsecond\t" + new string('x', 600), Now);

        Assert.StartsWith("first second x", entry.Details);
        Assert.Equal(AuditEntry.MaxDetailsLength, entry.Details!.Length);
        Assert.EndsWith("…", entry.Details);
    }

    [Fact]
    public void Blank_details_are_none()
    {
        var entry = AuditEntry.Record(Alice, AuditAction.SignedOut, AuditOutcome.Succeeded, null, "  \n ", Now);

        Assert.Null(entry.Details);
    }

    [Fact]
    public void Unknown_values_are_rejected()
    {
        Assert.Throws<DomainValidationException>(() =>
            AuditEntry.Record(Alice, (AuditAction)999, AuditOutcome.Succeeded, null, null, Now));
        Assert.Throws<DomainValidationException>(() =>
            AuditEntry.Record(Alice with { Channel = (AuditChannel)9 }, AuditAction.SignedIn, AuditOutcome.Succeeded, null, null, Now));
    }
}
