using System.Net.Http.Json;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>Agent offline and back online, from the agent's connection to the notification history.</summary>
public class AgentNotificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Agent_that_stays_away_is_notified_offline_and_then_back_online()
    {
        await using var app = new AggregatorApp
        {
            Customize = builder => builder.UseSetting("Notifications:AgentOfflineDelay", "00:00:00.2"),
        };
        await AddRuleForEverythingAsync(app);
        var agent = await app.EnrollAndConnectAsync();
        var environmentName = (await app.CreateClient().GetFromJsonAsync<AggregatorApp.EnvironmentView[]>("/api/environments", AggregatorApp.Json, Ct))!
            .Single(e => e.Id == agent.EnvironmentId).Name;

        await agent.DisposeAsync();
        var offline = await WaitForAsync(app, 1);
        Assert.Equal(NotificationEventKinds.AgentOffline, offline[0].Kind);
        Assert.Equal(agent.EnvironmentId, offline[0].EnvironmentId.Value);
        Assert.Equal($"The agent of '{environmentName}' went offline and has not reconnected.", offline[0].Summary);

        await using var back = await FakeAgent.ConnectAsync(app.Gateway, agent.Enrollment, "boot-1");
        var both = await WaitForAsync(app, 2);
        Assert.Equal(NotificationEventKinds.AgentOnline, both[1].Kind);
    }

    [Fact]
    public async Task Quick_reconnect_is_not_notified()
    {
        await using var app = new AggregatorApp
        {
            Customize = builder => builder.UseSetting("Notifications:AgentOfflineDelay", "00:00:05"),
        };
        await AddRuleForEverythingAsync(app);
        var agent = await app.EnrollAndConnectAsync();

        await agent.DisposeAsync();
        await app.WaitUntilOnlineAsync(agent.EnvironmentId, online: false);
        await using var back = await FakeAgent.ConnectAsync(app.Gateway, agent.Enrollment, "boot-1");
        await app.WaitUntilOnlineAsync(agent.EnvironmentId);

        Assert.Empty(await ListAsync(app));
    }

    private static async Task AddRuleForEverythingAsync(AggregatorApp app)
    {
        var channel = NotificationChannel.CreateDiscord("ops", 1_234_567_890_123_456_789, DateTimeOffset.UtcNow);
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<INotificationChannelRepository>().Add(channel);
        services.GetRequiredService<INotificationRuleRepository>().Add(
            NotificationRule.ForEverything(channel, NotificationEventKinds.All, DateTimeOffset.UtcNow));
        await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
    }

    /// <summary>Oldest first.</summary>
    private static async Task<List<Notification>> ListAsync(AggregatorApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var recent = await scope.ServiceProvider.GetRequiredService<INotificationRepository>().ListRecentAsync(50, Ct);
        return recent.OrderBy(n => n.CreatedAt).ToList();
    }

    private static async Task<List<Notification>> WaitForAsync(AggregatorApp app, int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var notifications = await ListAsync(app);
            if (notifications.Count >= count)
            {
                return notifications;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"Expected {count} notification(s).");
    }
}
