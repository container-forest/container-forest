using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.Extensions.DependencyInjection;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>The notification entities survive a round trip through SQLite with their mapping.</summary>
public class NotificationPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Channels_rules_and_notifications_round_trip()
    {
        await using var app = new AggregatorApp();
        const ulong discordChannelId = 1_234_567_890_123_456_789;
        var environment = DockerEnvironment.Create("prod", isPrivate: false, Now);
        var enabled = NotificationChannel.CreateDiscord("ops", discordChannelId, Now);
        var disabled = NotificationChannel.CreateDiscord("muted", 42, Now);
        disabled.Disable();
        var events = NotificationEventKinds.DeploymentFailed | NotificationEventKinds.AgentOffline;
        var rule = NotificationRule.ForEnvironment(enabled, environment, events, Now);
        var mutedRule = NotificationRule.ForEverything(disabled, NotificationEventKinds.All, Now);
        var notification = Notification.Create(
            rule, new NotificationEvent(NotificationEventKinds.AgentOffline, environment.Id, null, Now), "prod is offline", Now);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            services.GetRequiredService<IEnvironmentRepository>().Add(environment);
            services.GetRequiredService<INotificationChannelRepository>().Add(enabled);
            services.GetRequiredService<INotificationChannelRepository>().Add(disabled);
            services.GetRequiredService<INotificationRuleRepository>().Add(rule);
            services.GetRequiredService<INotificationRuleRepository>().Add(mutedRule);
            services.GetRequiredService<INotificationRepository>().Add(notification);
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var channel = await services.GetRequiredService<INotificationChannelRepository>().GetAsync(enabled.Id, Ct);
            var active = await services.GetRequiredService<INotificationRuleRepository>().ListActiveAsync(Ct);
            var pending = await services.GetRequiredService<INotificationRepository>().ListPendingAsync(Ct);

            Assert.Equal(discordChannelId, channel.DiscordChannelId);
            var loaded = Assert.Single(active);
            Assert.Equal(rule.Id, loaded.Id);
            Assert.Equal(events, loaded.Events);
            Assert.Equal(environment.Id, loaded.EnvironmentId);
            var loadedNotification = Assert.Single(pending);
            Assert.Equal(NotificationEventKinds.AgentOffline, loadedNotification.Kind);
            Assert.Equal(NotificationSeverity.Error, loadedNotification.Severity);
            Assert.Null(loadedNotification.StackId);
        }
    }

    [Fact]
    public async Task Resource_usage_details_round_trip()
    {
        await using var app = new AggregatorApp();
        var environment = DockerEnvironment.Create("prod", isPrivate: false, Now);
        var channel = NotificationChannel.CreateDiscord("ops", 42, Now);
        var rule = NotificationRule.ForEverything(channel, NotificationEventKinds.All, Now);
        var details = new ResourceUsageNotificationDetails(ResourceAlertKind.ContainerMemory, "shop-web-1", StackName.Parse("shop"), 93.5, 90);
        var notification = Notification.Create(
            rule, new NotificationEvent(NotificationEventKinds.ResourceUsageHigh, environment.Id, null, Now, details), "high", Now);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            services.GetRequiredService<IEnvironmentRepository>().Add(environment);
            services.GetRequiredService<INotificationChannelRepository>().Add(channel);
            services.GetRequiredService<INotificationRuleRepository>().Add(rule);
            services.GetRequiredService<INotificationRepository>().Add(notification);
            await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var loaded = Assert.Single(await scope.ServiceProvider.GetRequiredService<INotificationRepository>().ListPendingAsync(Ct));

            Assert.Equal(details, loaded.Details);
            Assert.Equal(NotificationSeverity.Warning, loaded.Severity);
        }
    }
}
