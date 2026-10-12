using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Aggregator.Application.Tests.Notifications;

public class ContainerIncidentNotifierTests
{
    private static readonly DateTimeOffset Now = NotificationFixture.Now;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly NotificationFixture fixture = new();

    [Fact]
    public async Task Crash_in_a_managed_stack_is_the_stacks_event_with_the_exit_code()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForStack(ops, fixture.Stack, NotificationEventKinds.ContainerDied, Now));

        await Notifier().NotifyAsync(Observation("die", "Exited (137) 1 second ago"), NotificationEventKinds.ContainerDied, Ct);

        var notification = Assert.Single(fixture.Notifications.All);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.Equal(fixture.Stack.Id, notification.StackId);
        Assert.Equal(Now, notification.OccurredAt);
        Assert.Equal("Container 'web-app-1' of stack 'web' on 'prod' exited with code 137.", notification.Summary);
        Assert.Equal(
            new ContainerNotificationDetails("0123456789abcdef", "web-app-1", fixture.Stack.Name, "app", 137),
            notification.Details);
    }

    [Fact]
    public async Task Container_killed_for_memory_says_so_instead_of_the_exit_code()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForEverything(ops, NotificationEventKinds.All, Now));

        await Notifier().NotifyAsync(
            Observation("die", "Exited (137) 1 second ago") with { OutOfMemory = true }, NotificationEventKinds.ContainerDied, Ct);

        var notification = Assert.Single(fixture.Notifications.All);
        Assert.Equal("Container 'web-app-1' of stack 'web' on 'prod' was killed: out of memory.", notification.Summary);
        Assert.Equal(
            new ContainerNotificationDetails("0123456789abcdef", "web-app-1", fixture.Stack.Name, "app", 137, OutOfMemory: true),
            notification.Details);
    }

    [Fact]
    public async Task Compose_project_that_is_not_a_managed_stack_is_the_environments_event()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForEnvironment(ops, fixture.Environment, NotificationEventKinds.All, Now));
        fixture.AddRule(NotificationRule.ForStack(fixture.AddChannel("web-team", 2000), fixture.Stack, NotificationEventKinds.All, Now));

        await Notifier().NotifyAsync(
            Observation("die", "Up 1 second", stack: "monitoring"), NotificationEventKinds.ContainerDied, Ct);

        var notification = Assert.Single(fixture.Notifications.All);
        Assert.Null(notification.StackId);
        Assert.Equal("Container 'web-app-1' of stack 'monitoring' on 'prod' stopped unexpectedly.", notification.Summary);
    }

    [Fact]
    public async Task Unhealthy_container_outside_compose_is_a_warning_without_an_exit_code()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForEverything(ops, NotificationEventKinds.All, Now));

        await Notifier().NotifyAsync(
            Observation("health_status: unhealthy", "Exited (1) 1 second ago", stack: null, service: null),
            NotificationEventKinds.ContainerUnhealthy,
            Ct);

        var notification = Assert.Single(fixture.Notifications.All);
        Assert.Equal(NotificationSeverity.Warning, notification.Severity);
        Assert.Equal("Container 'web-app-1' on 'prod' is unhealthy.", notification.Summary);
        var details = Assert.IsType<ContainerNotificationDetails>(notification.Details);
        Assert.Null(details.StackName);
        Assert.Null(details.ServiceName);
        Assert.Null(details.ExitCode);
    }

    [Fact]
    public async Task Deleted_environment_is_not_notified_about()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForEverything(ops, NotificationEventKinds.All, Now));

        var created = await Notifier().NotifyAsync(
            Observation("die", null) with { EnvironmentId = EnvironmentId.New() }, NotificationEventKinds.ContainerDied, Ct);

        Assert.Empty(created);
    }

    [Theory]
    [InlineData("Exited (0) 5 seconds ago", 0)]
    [InlineData("Exited (137) About a minute ago", 137)]
    [InlineData("Up 3 minutes (unhealthy)", null)]
    [InlineData("Restarting (1) 2 seconds ago", 1)]
    [InlineData(null, null)]
    public void Exit_code_is_read_from_dockers_status(string? status, int? exitCode) =>
        Assert.Equal(exitCode, Observation("die", status).ExitCode);

    [Fact]
    public void Container_that_is_gone_is_named_by_its_short_id()
    {
        var gone = Observation("die", null) with { ContainerName = null };

        Assert.Equal("0123456789ab", gone.DisplayName);
    }

    private ContainerObservation Observation(string action, string? status, string? stack = "web", string? service = "app") =>
        new(fixture.Environment.Id, "0123456789abcdef", action, Now, "web-app-1", stack, service, status);

    private ContainerIncidentNotifier Notifier() =>
        new(fixture.Dispatcher(), fixture.Environments, fixture.Stacks, NullLogger<ContainerIncidentNotifier>.Instance);
}
