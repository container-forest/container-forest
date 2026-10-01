extern alias bot;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using bot::ContainerForest.DiscordBot.Forest;
using bot::ContainerForest.DiscordBot.Notifications;
using ContainerForest.DiscordBot.Tests.Forest;

namespace ContainerForest.DiscordBot.Tests.Notifications;

public sealed class NotificationWatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeNotificationSource source = new();
    private readonly FakeNotificationPoster poster = new();

    [Fact]
    public async Task PostsEachNotification_AndReportsTheOutcome()
    {
        poster.Results["denied"] = DeliveryResult.Failed("Missing Permissions");
        source.Send(Notification("ok"), Notification("denied"));

        await RunUntilAsync(() => source.Reports.Count == 2);

        Assert.Equal(["ok", "denied"], poster.Posted.Select(n => n.Id));
        Assert.Equal([("ok", true, null), ("denied", false, "Missing Permissions")], source.Reports);
    }

    [Fact]
    public async Task WaitsForDiscord_BeforeOpeningTheStream()
    {
        poster.Ready = new TaskCompletionSource();
        source.Send(Notification("ok"));
        await using var watcher = await StartAsync();

        await Task.Delay(100, Ct);
        Assert.Equal(0, source.Opened);

        poster.Ready.SetResult();
        await WaitUntilAsync(() => source.Reports.Count == 1);
    }

    [Fact]
    public async Task PosterBug_IsReportedAsAFailure_AndDeliveryGoesOn()
    {
        poster.Throws.Add("boom");
        source.Send(Notification("boom"), Notification("ok"));

        await RunUntilAsync(() => source.Reports.Count == 2);

        Assert.Equal([("boom", false, NotificationWatcher.BotFailureError), ("ok", true, null)], source.Reports);
    }

    [Fact]
    public async Task EndedOrFailedStream_IsReopened()
    {
        source.Send(Notification("first"));
        source.Fail(new ForestUnavailableException("The aggregator is unreachable: connection refused"));
        source.Send(Notification("second"));

        await RunUntilAsync(() => source.Reports.Count == 2);

        Assert.Equal(["first", "second"], source.Reports.Select(r => r.Id));
        Assert.True(source.Opened >= 3);
    }

    [Fact]
    public async Task FailedReport_DoesNotStopDelivery()
    {
        source.FailReportsFor.Add("lost");
        source.Send(Notification("lost"), Notification("ok"));

        await RunUntilAsync(() => source.Reports.Count == 1);

        Assert.Equal(["lost", "ok"], poster.Posted.Select(n => n.Id));
        Assert.Equal([("ok", true, null)], source.Reports);
    }

    private static ForestNotification Notification(string id) => new(
        id, 42, NotificationKind.DeploymentFailed, NotificationSeverity.Error, DateTimeOffset.UnixEpoch, "env-1", "prod", "Deployment failed");

    private async Task RunUntilAsync(Func<bool> condition)
    {
        await using var watcher = await StartAsync();
        await WaitUntilAsync(condition);
    }

    private async Task<RunningWatcher> StartAsync()
    {
        var watcher = new NotificationWatcher(
            source, poster, new ReconnectBackoff(TimeSpan.Zero, TimeSpan.Zero), TimeProvider.System, new RecordingLogger<NotificationWatcher>());
        await watcher.StartAsync(Ct);
        return new RunningWatcher(watcher);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(25, Ct);
        }

        Assert.True(condition(), "The watcher did not get there in time.");
    }

    private sealed class RunningWatcher(NotificationWatcher watcher) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await watcher.StopAsync(CancellationToken.None);
            watcher.Dispose();
        }
    }
}

/// <summary>
/// Each WatchAsync call plays the next scripted stream: one that sends notifications and ends (<see cref="Send"/>)
/// or one that fails at once (<see cref="Fail"/>). With nothing left it waits until cancelled, like an idle stream.
/// </summary>
internal sealed class FakeNotificationSource : INotificationSource
{
    private readonly Channel<Func<IReadOnlyList<ForestNotification>>> streams = Channel.CreateUnbounded<Func<IReadOnlyList<ForestNotification>>>();
    private int opened;

    public HashSet<string> FailReportsFor { get; } = [];

    public List<(string Id, bool Delivered, string? Error)> Reports { get; } = [];

    public int Opened => Volatile.Read(ref opened);

    public void Send(params ForestNotification[] notifications) => streams.Writer.TryWrite(() => notifications);

    public void Fail(ForestException failure) => streams.Writer.TryWrite(() => throw failure);

    public async IAsyncEnumerable<ForestNotification> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref opened);
        var stream = await streams.Reader.ReadAsync(cancellationToken);
        foreach (var notification in stream())
        {
            yield return notification;
        }
    }

    public Task ReportAsync(string notificationId, bool delivered, string? error, CancellationToken cancellationToken)
    {
        if (FailReportsFor.Contains(notificationId))
        {
            throw new ForestUnavailableException("The aggregator is unreachable: connection reset");
        }

        lock (Reports)
        {
            Reports.Add((notificationId, delivered, error));
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeNotificationPoster : INotificationPoster
{
    public TaskCompletionSource? Ready { get; set; }

    public Dictionary<string, DeliveryResult> Results { get; } = [];

    public HashSet<string> Throws { get; } = [];

    public List<ForestNotification> Posted { get; } = [];

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        Ready is null ? Task.CompletedTask : Ready.Task.WaitAsync(cancellationToken);

    public Task<DeliveryResult> PostAsync(ForestNotification notification, CancellationToken cancellationToken)
    {
        lock (Posted)
        {
            Posted.Add(notification);
        }

        return Throws.Contains(notification.Id)
            ? throw new InvalidOperationException("Discord.Net went sideways")
            : Task.FromResult(Results.GetValueOrDefault(notification.Id, DeliveryResult.Delivered));
    }
}
