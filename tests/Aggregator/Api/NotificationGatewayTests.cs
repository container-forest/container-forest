using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Contracts.Bot;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using DomainNotification = ContainerForest.Aggregator.Domain.Notifications.Notification;
using Notification = ContainerForest.Contracts.Bot.Notification;
using NotificationSeverity = ContainerForest.Contracts.Bot.NotificationSeverity;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>Delivery through the bot: WatchNotifications and ReportDelivery of bot.proto.</summary>
public class NotificationGatewayTests
{
    private const ulong DiscordChannelId = 1_234_567_890_123_456_789;

    private static readonly CommitSha Commit = CommitSha.Parse("0123456789abcdef0123456789abcdef01234567");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Metadata BotHeaders(string token = AggregatorApp.BotToken) => new() { { "x-bot-token", token } };

    [Fact]
    public async Task Stream_without_the_bot_token_is_rejected()
    {
        await using var app = new AggregatorApp();
        using var call = app.BotGateway.WatchNotifications(new WatchNotificationsRequest(), BotHeaders("wrong-token"), cancellationToken: Ct);

        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(Ct));

        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        Assert.Equal("invalid-bot-token", error.Trailers.GetValue("x-cf-reason"));
    }

    [Fact]
    public async Task Pending_and_new_notifications_are_streamed_and_reports_finish_them()
    {
        await using var app = new AggregatorApp();
        var environment = await SeedAsync(app);
        var pending = await DispatchFailedDeploymentAsync(app, environment);

        using var call = app.BotGateway.WatchNotifications(new WatchNotificationsRequest(), BotHeaders(), cancellationToken: Ct);
        var first = await NextAsync(call);

        Assert.Equal(pending.Id.ToString(), first.Id);
        Assert.Equal(DiscordChannelId, first.DiscordChannelId);
        Assert.Equal(NotificationKind.DeploymentFailed, first.Kind);
        Assert.Equal(NotificationSeverity.Error, first.Severity);
        Assert.Equal("prod", first.EnvironmentName);
        Assert.Equal(environment.Id.ToString(), first.EnvironmentId);
        Assert.Equal(pending.Summary, first.Summary);
        Assert.Equal(Notification.DetailsOneofCase.Deployment, first.DetailsCase);
        Assert.Equal("web", first.Deployment.StackName);
        Assert.Equal(Commit.Value, first.Deployment.CommitSha);
        Assert.Equal("compose failed", first.Deployment.Error);

        // Created while the stream is open: the dispatcher wakes it, no rescan interval to wait for.
        var later = await DispatchFailedDeploymentAsync(app, environment);
        var second = await NextAsync(call);
        Assert.Equal(later.Id.ToString(), second.Id);

        await app.BotGateway.ReportDeliveryAsync(
            new ReportDeliveryRequest { NotificationId = first.Id, Delivered = true }, BotHeaders(), cancellationToken: Ct);
        await app.BotGateway.ReportDeliveryAsync(
            new ReportDeliveryRequest { NotificationId = second.Id, Delivered = false, Error = "Missing Permissions" },
            BotHeaders(),
            cancellationToken: Ct);

        var delivered = await FindAsync(app, pending.Id);
        var failed = await FindAsync(app, later.Id);
        Assert.Equal(NotificationStatus.Delivered, delivered.Status);
        Assert.Equal(NotificationStatus.Failed, failed.Status);
        Assert.Equal("Missing Permissions", failed.Error);
    }

    [Fact]
    public async Task Unreported_notification_is_sent_again_by_the_call_that_replaces_the_stream()
    {
        await using var app = new AggregatorApp();
        var environment = await SeedAsync(app);
        var pending = await DispatchFailedDeploymentAsync(app, environment);

        using var first = app.BotGateway.WatchNotifications(new WatchNotificationsRequest(), BotHeaders(), cancellationToken: Ct);
        Assert.Equal(pending.Id.ToString(), (await NextAsync(first)).Id);

        using var second = app.BotGateway.WatchNotifications(new WatchNotificationsRequest(), BotHeaders(), cancellationToken: Ct);
        Assert.Equal(pending.Id.ToString(), (await NextAsync(second)).Id);

        var replaced = await Assert.ThrowsAsync<RpcException>(() => first.ResponseStream.MoveNext(Ct));
        Assert.Equal(StatusCode.Aborted, replaced.StatusCode);
    }

    [Fact]
    public async Task Report_needs_a_known_notification_id()
    {
        await using var app = new AggregatorApp();

        var malformed = await Assert.ThrowsAsync<RpcException>(() => app.BotGateway.ReportDeliveryAsync(
            new ReportDeliveryRequest { NotificationId = "nope", Delivered = true }, BotHeaders(), cancellationToken: Ct).ResponseAsync);
        var unknown = await Assert.ThrowsAsync<RpcException>(() => app.BotGateway.ReportDeliveryAsync(
            new ReportDeliveryRequest { NotificationId = Guid.NewGuid().ToString(), Delivered = true }, BotHeaders(), cancellationToken: Ct).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, malformed.StatusCode);
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
    }

    /// <summary>An environment "prod" and a channel that receives everything from it.</summary>
    private static async Task<DockerEnvironment> SeedAsync(AggregatorApp app)
    {
        var environment = DockerEnvironment.Create("prod", isPrivate: false, DateTimeOffset.UtcNow);
        var channel = NotificationChannel.CreateDiscord("ops", DiscordChannelId, DateTimeOffset.UtcNow);
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<IEnvironmentRepository>().Add(environment);
        services.GetRequiredService<INotificationChannelRepository>().Add(channel);
        services.GetRequiredService<INotificationRuleRepository>().Add(
            NotificationRule.ForEnvironment(channel, environment, NotificationEventKinds.All, DateTimeOffset.UtcNow));
        await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        return environment;
    }

    private static async Task<DomainNotification> DispatchFailedDeploymentAsync(AggregatorApp app, DockerEnvironment environment)
    {
        var stackId = StackId.New();
        var details = new DeploymentNotificationDetails(DeploymentId.New(), stackId, StackName.Parse("web"), Commit, false, "compose failed");
        var notificationEvent = new NotificationEvent(
            NotificationEventKinds.DeploymentFailed, environment.Id, stackId, DateTimeOffset.UtcNow, details);

        await using var scope = app.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>()
            .DispatchAsync(notificationEvent, "Deployment of stack 'web' on 'prod' failed: compose failed", Ct);
        return Assert.Single(created);
    }

    private static async Task<DomainNotification> FindAsync(AggregatorApp app, NotificationId id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INotificationRepository>().GetAsync(id, Ct);
    }

    private static async Task<Notification> NextAsync(AsyncServerStreamingCall<Notification> call)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token), "The stream ended.");
        return call.ResponseStream.Current;
    }
}
