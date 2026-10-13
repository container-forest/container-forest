using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Stacks;
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
        Assert.Equal("Container 'web-app-1' of stack 'web' on 'prod' exited with code 137 (SIGKILL).", notification.Summary);
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
    public async Task Container_with_the_system_label_or_of_a_system_stack_is_marked_as_one()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForEverything(ops, NotificationEventKinds.All, Now));
        var system = Stack.Create(
            fixture.Environment.Id, StackName.Parse("forest"), RepositoryId.New(), BranchName.Parse("main"),
            StackFilePath.Parse("compose.yaml"), isSystem: true, Now);
        fixture.Stacks.Add(system);

        await Notifier().NotifyAsync(
            Observation("die", "Exited (1) 1 second ago", stack: null) with { IsSystem = true }, NotificationEventKinds.ContainerDied, Ct);
        await Notifier().NotifyAsync(Observation("die", "Exited (1) 1 second ago", stack: "forest"), NotificationEventKinds.ContainerDied, Ct);
        await Notifier().NotifyAsync(Observation("die", "Exited (1) 1 second ago"), NotificationEventKinds.ContainerDied, Ct);

        Assert.Equal(
            [true, true, false],
            fixture.Notifications.All.Select(n => Assert.IsType<ContainerNotificationDetails>(n.Details).IsSystem).ToList());
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
    public async Task Healthy_again_is_info_without_an_exit_code()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForStack(ops, fixture.Stack, NotificationEventKinds.ContainerHealthy, Now));

        await Notifier().NotifyAsync(
            Observation("health_status: healthy", "Up 5 minutes (healthy)"), NotificationEventKinds.ContainerHealthy, Ct);

        var notification = Assert.Single(fixture.Notifications.All);
        Assert.Equal(NotificationSeverity.Info, notification.Severity);
        Assert.Equal("Container 'web-app-1' of stack 'web' on 'prod' is healthy again.", notification.Summary);
        Assert.Null(Assert.IsType<ContainerNotificationDetails>(notification.Details).ExitCode);
    }

    [Fact]
    public void Container_that_is_gone_is_named_by_its_short_id()
    {
        var gone = Observation("die", null) with { ContainerName = null };

        Assert.Equal("0123456789ab", gone.DisplayName);
    }

    [Theory]
    [InlineData(139, "exited with code 139 (SIGSEGV).")]
    [InlineData(143, "exited with code 143 (SIGTERM).")]
    [InlineData(134, "exited with code 134 (SIGABRT).")]
    [InlineData(1, "exited with code 1.")]
    [InlineData(128, "exited with code 128.")]
    [InlineData(255, "exited with code 255.")]
    [InlineData(-1, "exited with code -1.")]
    public void Exit_code_of_a_signal_is_named(int exitCode, string ending)
    {
        var details = new ContainerNotificationDetails("0123456789abcdef", "web-app-1", null, null, exitCode);

        var summary = ContainerIncidentNotifier.Summarize(NotificationEventKinds.ContainerDied, details, "prod");

        Assert.Equal($"Container 'web-app-1' on 'prod' {ending}", summary);
    }

    private ContainerObservation Observation(string action, string? status, string? stack = "web", string? service = "app") =>
        new(fixture.Environment.Id, "0123456789abcdef", action, Now, "web-app-1", stack, service, status);

    private ContainerIncidentNotifier Notifier() =>
        new(fixture.Dispatcher(), fixture.Environments, fixture.Stacks, NullLogger<ContainerIncidentNotifier>.Instance);
}
