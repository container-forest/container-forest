using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Aggregator.Application.Tests.Notifications;

public sealed class AgentConnectivityWatchTests : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(100);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly NotificationFixture fixture = new();
    private readonly AgentConnectivityWatch watch;

    public AgentConnectivityWatchTests()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddRule(NotificationRule.ForEverything(ops, NotificationEventKinds.All, NotificationFixture.Now));
        watch = new AgentConnectivityWatch(
            new NotifierScopes(fixture),
            new NotificationOptions { AgentOfflineDelay = Delay },
            TimeProvider.System,
            NullLogger<AgentConnectivityWatch>.Instance);
    }

    public void Dispose() => watch.Dispose();

    [Fact]
    public async Task Reconnect_within_the_delay_is_not_notified()
    {
        var id = fixture.Environment.Id;

        watch.Disconnected(id, DateTimeOffset.UtcNow);
        watch.Connected(id, DateTimeOffset.UtcNow);
        await Task.Delay(Delay * 4, Ct);

        Assert.Empty(Kinds());
    }

    [Fact]
    public async Task Outage_longer_than_the_delay_is_offline_then_back_online()
    {
        var id = fixture.Environment.Id;
        var lastSeen = DateTimeOffset.UtcNow;

        watch.Disconnected(id, lastSeen);
        await WaitForAsync(1);
        watch.Connected(id, DateTimeOffset.UtcNow);
        await WaitForAsync(2);

        Assert.Equal([NotificationEventKinds.AgentOffline, NotificationEventKinds.AgentOnline], Kinds());
        Assert.Equal(lastSeen, fixture.Notifications.All[0].OccurredAt);
    }

    [Fact]
    public async Task Connection_without_an_outage_is_not_notified()
    {
        watch.Connected(fixture.Environment.Id, DateTimeOffset.UtcNow);
        await Task.Delay(Delay * 2, Ct);

        Assert.Empty(Kinds());
    }

    [Fact]
    public async Task Flapping_agent_is_one_outage_counted_from_the_first_disconnect()
    {
        var id = fixture.Environment.Id;
        var firstLost = DateTimeOffset.UtcNow;

        // A second disconnect while the first is waited out does not restart the wait.
        watch.Disconnected(id, firstLost);
        watch.Disconnected(id, firstLost.AddSeconds(1));
        await WaitForAsync(1);
        await Task.Delay(Delay * 2, Ct);

        var notification = Assert.Single(fixture.Notifications.All);
        Assert.Equal(firstLost, notification.OccurredAt);
    }

    [Fact]
    public async Task Aggregator_shutdown_cancels_the_outages_being_waited_out()
    {
        watch.Disconnected(fixture.Environment.Id, DateTimeOffset.UtcNow);
        watch.Dispose();
        await Task.Delay(Delay * 4, Ct);

        Assert.Empty(Kinds());
    }

    private List<NotificationEventKinds> Kinds()
    {
        return fixture.Notifications.All.Select(n => n.Kind).ToList();
    }

    private async Task WaitForAsync(int count)
    {
        for (var attempt = 0; attempt < 100 && Kinds().Count < count; attempt++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.Equal(count, Kinds().Count);
    }
}
