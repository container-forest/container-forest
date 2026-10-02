using ContainerForest.Aggregator.Application.Notifications;

namespace ContainerForest.Aggregator.Application.Tests.Notifications;

public class NotificationFeedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Wait_ends_on_publish()
    {
        var feed = new NotificationFeed();
        using var subscription = feed.Subscribe(Ct);

        var wait = subscription.WaitAsync(TimeSpan.FromMinutes(5));
        feed.Publish();

        Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5), Ct)));
    }

    [Fact]
    public async Task Wait_ends_on_timeout_without_ending_the_subscription()
    {
        var feed = new NotificationFeed();
        using var subscription = feed.Subscribe(Ct);

        await subscription.WaitAsync(TimeSpan.FromMilliseconds(20));

        Assert.False(subscription.Ended.IsCancellationRequested);
    }

    [Fact]
    public async Task New_subscription_replaces_the_previous_one()
    {
        var feed = new NotificationFeed();
        using var first = feed.Subscribe(Ct);
        var waiting = first.WaitAsync(TimeSpan.FromMinutes(5));

        using var second = feed.Subscribe(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(first.WasReplaced);
        Assert.False(second.Ended.IsCancellationRequested);
    }

    [Fact]
    public async Task Ended_call_ends_the_subscription_without_replacing_it()
    {
        var feed = new NotificationFeed();
        using var call = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var subscription = feed.Subscribe(call.Token);

        await call.CancelAsync();

        Assert.True(subscription.Ended.IsCancellationRequested);
        Assert.False(subscription.WasReplaced);
    }

    [Fact]
    public void Publishing_without_a_subscriber_or_after_it_left_does_nothing()
    {
        var feed = new NotificationFeed();
        feed.Publish();
        feed.Subscribe(Ct).Dispose();
        feed.Publish();
    }
}
