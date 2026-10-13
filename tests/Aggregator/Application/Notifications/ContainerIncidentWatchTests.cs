using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Aggregator.Application.Tests.Notifications;

public sealed class ContainerIncidentWatchTests : IDisposable
{
    private static readonly TimeSpan StopWait = TimeSpan.FromMilliseconds(100);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly NotificationFixture fixture = new();
    private readonly ContainerIncidentWatch watch;

    public ContainerIncidentWatchTests()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForEverything(ops, NotificationEventKinds.All, NotificationFixture.Now));
        watch = Watch(TimeSpan.FromMinutes(10));
    }

    public void Dispose() => watch.Dispose();

    [Fact]
    public async Task Die_without_a_stop_is_a_crash()
    {
        watch.Observe(Event("die", "Exited (1) 1 second ago"));
        await WaitForAsync(1);

        var notification = Assert.Single(fixture.Notifications.All);
        Assert.Equal(NotificationEventKinds.ContainerDied, notification.Kind);
        Assert.Equal(1, Assert.IsType<ContainerNotificationDetails>(notification.Details).ExitCode);
    }

    [Fact]
    public async Task Stop_before_the_die_was_asked_for()
    {
        // docker stop, restart and compose down: Docker reports kill (not forwarded by the agent), stop, die.
        watch.Observe(Event("stop", "Exited (143) 1 second ago"));
        watch.Observe(Event("die", "Exited (143) 1 second ago"));
        await Task.Delay(StopWait * 4, Ct);

        Assert.Empty(fixture.Notifications.All);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("destroy")]
    public async Task Stop_or_destroy_after_the_die_was_asked_for(string action)
    {
        // docker rm -f: kill, die, destroy.
        watch.Observe(Event("die", "Exited (137) 1 second ago"));
        watch.Observe(Event(action, "Exited (137) 1 second ago"));
        await Task.Delay(StopWait * 4, Ct);

        Assert.Empty(fixture.Notifications.All);
    }

    [Fact]
    public async Task Old_stop_does_not_hide_a_later_crash()
    {
        watch.Observe(Event("stop", "Exited (0) 1 second ago"));
        await Task.Delay(StopWait * 2, Ct);
        watch.Observe(Event("die", "Exited (1) 1 second ago"));
        await WaitForAsync(1);
    }

    [Fact]
    public async Task Container_that_finished_its_job_is_not_a_crash()
    {
        watch.Observe(Event("die", "Exited (0) 1 second ago"));
        await Task.Delay(StopWait * 4, Ct);

        Assert.Empty(fixture.Notifications.All);
    }

    [Fact]
    public async Task Crash_already_restarted_by_docker_is_notified_without_an_exit_code()
    {
        // With a restart policy the agent may find the container running again when it looks it up, and an older agent
        // does not send the code from the event.
        watch.Observe(Event("die", "Up Less than a second"));
        await WaitForAsync(1);

        Assert.Null(Assert.IsType<ContainerNotificationDetails>(fixture.Notifications.All[0].Details).ExitCode);
    }

    [Fact]
    public async Task Crash_already_restarted_by_docker_keeps_the_exit_code_of_the_event()
    {
        watch.Observe(Event("die", "Up Less than a second") with { ReportedExitCode = 3 });
        await WaitForAsync(1);

        Assert.Equal(3, Assert.IsType<ContainerNotificationDetails>(fixture.Notifications.All[0].Details).ExitCode);
    }

    [Fact]
    public async Task Job_that_finished_and_was_started_again_is_not_a_crash()
    {
        // restart: always on a container that exits 0: the status no longer says so, the event does.
        watch.Observe(Event("die", "Up Less than a second") with { ReportedExitCode = 0 });
        await Task.Delay(StopWait * 4, Ct);

        Assert.Empty(fixture.Notifications.All);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oom_with_the_death_marks_it_killed_for_memory(bool oomFirst)
    {
        // Docker reports oom before die; the other order is handled too.
        if (oomFirst)
        {
            watch.Observe(Event("oom", "Up 5 minutes"));
        }

        watch.Observe(Event("die", "Exited (137) 1 second ago"));
        if (!oomFirst)
        {
            watch.Observe(Event("oom", "Exited (137) 1 second ago"));
        }

        await WaitForAsync(1);

        var details = Assert.IsType<ContainerNotificationDetails>(fixture.Notifications.All[0].Details);
        Assert.True(details.OutOfMemory);
        Assert.Equal(137, details.ExitCode);
    }

    [Fact]
    public async Task Oom_without_a_death_is_not_notified_and_does_not_mark_a_later_crash()
    {
        // The kernel killed another process of the container; the container itself lives on.
        watch.Observe(Event("oom", "Up 5 minutes"));
        await Task.Delay(StopWait * 2, Ct);
        Assert.Empty(fixture.Notifications.All);

        watch.Observe(Event("die", "Exited (1) 1 second ago"));
        await WaitForAsync(1);

        Assert.False(Assert.IsType<ContainerNotificationDetails>(fixture.Notifications.All[0].Details).OutOfMemory);
    }

    [Fact]
    public async Task Requested_stop_is_not_a_crash_even_with_an_oom()
    {
        watch.Observe(Event("oom", "Up 5 minutes"));
        watch.Observe(Event("stop", "Exited (137) 1 second ago"));
        watch.Observe(Event("die", "Exited (137) 1 second ago"));
        await Task.Delay(StopWait * 4, Ct);

        Assert.Empty(fixture.Notifications.All);
    }

    [Fact]
    public async Task Unhealthy_is_notified_right_away_and_healthy_is_not()
    {
        watch.Observe(Event("health_status: healthy", "Up 1 minute (healthy)"));
        watch.Observe(Event("health_status: unhealthy", "Up 2 minutes (unhealthy)"));
        await WaitForAsync(1);

        Assert.Equal(NotificationEventKinds.ContainerUnhealthy, Assert.Single(fixture.Notifications.All).Kind);
    }

    [Fact]
    public async Task Restart_loop_is_one_message_per_interval()
    {
        for (var crash = 0; crash < 3; crash++)
        {
            watch.Observe(Event("die", "Exited (1) 1 second ago"));
            await Task.Delay(StopWait * 3, Ct);
            watch.Observe(Event("start", "Up Less than a second"));
        }

        watch.Observe(Event("health_status: unhealthy", "Up 1 second (unhealthy)"));
        await WaitForAsync(2);

        Assert.Equal(
            [NotificationEventKinds.ContainerDied, NotificationEventKinds.ContainerUnhealthy],
            fixture.Notifications.All.Select(n => n.Kind).ToList());
    }

    [Fact]
    public async Task Without_a_repeat_interval_every_crash_is_notified()
    {
        using var everyCrash = Watch(TimeSpan.Zero);

        everyCrash.Observe(Event("die", "Exited (1) 1 second ago"));
        await WaitForAsync(1);
        everyCrash.Observe(Event("die", "Exited (1) 1 second ago"));
        await WaitForAsync(2);
    }

    [Fact]
    public async Task Aggregator_shutdown_cancels_the_deaths_being_waited_out()
    {
        watch.Observe(Event("die", "Exited (1) 1 second ago"));
        watch.Dispose();
        await Task.Delay(StopWait * 4, Ct);

        Assert.Empty(fixture.Notifications.All);
    }

    private ContainerIncidentWatch Watch(TimeSpan repeatInterval) => new(
        new NotifierScopes(fixture),
        new NotificationOptions { ContainerStopWait = StopWait, ContainerRepeatInterval = repeatInterval },
        TimeProvider.System,
        NullLogger<ContainerIncidentWatch>.Instance);

    private ContainerObservation Event(string action, string status) =>
        new(fixture.Environment.Id, "0123456789abcdef", action, DateTimeOffset.UtcNow, "web-app-1", "web", "app", status);

    private async Task WaitForAsync(int count)
    {
        for (var attempt = 0; attempt < 100 && fixture.Notifications.All.Count < count; attempt++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal(count, fixture.Notifications.All.Count);
    }
}
