using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;

namespace ContainerForest.Aggregator.Application.Tests.Notifications;

public class NotificationDeliveryServiceTests
{
    private static readonly DateTimeOffset Now = NotificationFixture.Now;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly NotificationFixture fixture = new();

    [Fact]
    public async Task Pending_notifications_go_out_oldest_first_with_their_channel_and_environment()
    {
        var ops = fixture.AddChannel("ops", discordChannelId: 42);
        var newer = fixture.AddPending(ops, Now.AddSeconds(2));
        var older = fixture.AddPending(ops, Now.AddSeconds(1));

        var batch = await fixture.Delivery(Now.AddMinutes(1)).TakePendingAsync(new HashSet<NotificationId>(), Ct);

        Assert.Equal([older.Id, newer.Id], batch.Select(o => o.Notification.Id));
        Assert.All(batch, o => Assert.Equal(42ul, o.DiscordChannelId));
        Assert.All(batch, o => Assert.Equal("prod", o.EnvironmentName));
    }

    [Fact]
    public async Task Moving_a_channel_moves_its_pending_notifications()
    {
        var ops = fixture.AddChannel("ops", discordChannelId: 42);
        fixture.AddPending(ops);
        ops.MoveToDiscordChannel(43);

        var outgoing = Assert.Single(await fixture.Delivery().TakePendingAsync(new HashSet<NotificationId>(), Ct));

        Assert.Equal(43ul, outgoing.DiscordChannelId);
    }

    [Fact]
    public async Task Notifications_awaiting_a_report_are_not_sent_twice()
    {
        var ops = fixture.AddChannel("ops");
        var sent = fixture.AddPending(ops);
        var fresh = fixture.AddPending(ops);

        var batch = await fixture.Delivery().TakePendingAsync(new HashSet<NotificationId> { sent.Id }, Ct);

        Assert.Equal(fresh.Id, Assert.Single(batch).Notification.Id);
        Assert.Equal(NotificationStatus.Pending, sent.Status);
    }

    [Fact]
    public async Task Stale_notifications_expire_instead_of_going_out()
    {
        var ops = fixture.AddChannel("ops");
        var stale = fixture.AddPending(ops, Now);

        var batch = await fixture.Delivery(Now + Notification.MaxPendingAge).TakePendingAsync(new HashSet<NotificationId>(), Ct);

        Assert.Empty(batch);
        Assert.Equal(NotificationStatus.Expired, stale.Status);
        Assert.Equal(1, fixture.UnitOfWork.Saves);
    }

    [Fact]
    public async Task Notifications_of_a_deleted_or_disabled_channel_fail()
    {
        var deleted = fixture.AddChannel("deleted");
        var disabled = fixture.AddChannel("disabled");
        var orphan = fixture.AddPending(deleted);
        var muted = fixture.AddPending(disabled);
        fixture.Channels.Remove(deleted);
        disabled.Disable();

        var batch = await fixture.Delivery().TakePendingAsync(new HashSet<NotificationId>(), Ct);

        Assert.Empty(batch);
        Assert.Equal(NotificationDeliveryService.ChannelDeletedError, orphan.Error);
        Assert.Equal(NotificationDeliveryService.ChannelDisabledError, muted.Error);
        Assert.All([orphan, muted], n => Assert.Equal(NotificationStatus.Failed, n.Status));
    }

    [Fact]
    public async Task Deleted_environment_is_named_by_its_id()
    {
        var ops = fixture.AddChannel("ops");
        fixture.AddPending(ops);
        fixture.Environments.Remove(fixture.Environment);

        var outgoing = Assert.Single(await fixture.Delivery().TakePendingAsync(new HashSet<NotificationId>(), Ct));

        Assert.Equal(fixture.Environment.Id.ToString(), outgoing.EnvironmentName);
    }

    [Fact]
    public async Task Reports_finish_the_notification()
    {
        var ops = fixture.AddChannel("ops");
        var delivered = fixture.AddPending(ops);
        var failed = fixture.AddPending(ops);
        var unexplained = fixture.AddPending(ops);
        var delivery = fixture.Delivery(Now.AddSeconds(5));

        await delivery.ReportAsync(delivered.Id, delivered: true, error: "ignored", Ct);
        await delivery.ReportAsync(failed.Id, delivered: false, error: " Missing Permissions ", Ct);
        await delivery.ReportAsync(unexplained.Id, delivered: false, error: "", Ct);

        Assert.Equal(NotificationStatus.Delivered, delivered.Status);
        Assert.Null(delivered.Error);
        Assert.Equal(Now.AddSeconds(5), delivered.FinishedAt);
        Assert.Equal("Missing Permissions", failed.Error);
        Assert.Equal(NotificationDeliveryService.UnknownBotError, unexplained.Error);
    }

    [Fact]
    public async Task Report_for_a_finished_notification_is_ignored()
    {
        var ops = fixture.AddChannel("ops");
        var notification = fixture.AddPending(ops);
        notification.Expire(Now);

        await fixture.Delivery().ReportAsync(notification.Id, delivered: true, error: null, Ct);

        Assert.Equal(NotificationStatus.Expired, notification.Status);
        Assert.Equal(0, fixture.UnitOfWork.Saves);
    }

    [Fact]
    public async Task Report_for_an_unknown_notification_is_not_found() =>
        await Assert.ThrowsAsync<NotFoundException>(() =>
            fixture.Delivery().ReportAsync(NotificationId.New(), delivered: true, error: null, Ct));
}
