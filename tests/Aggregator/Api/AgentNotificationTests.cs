using System.Net.Http.Json;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Contracts.Agent;
using ContainerForest.Contracts.Bot;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using BotNotification = ContainerForest.Contracts.Bot.Notification;
using Notification = ContainerForest.Aggregator.Domain.Notifications.Notification;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>Notifications raised by what agents report: going offline and back, crashed containers.</summary>
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

    [Fact]
    public async Task Crashed_container_reaches_the_bot_with_its_details()
    {
        await using var app = new AggregatorApp
        {
            Customize = builder => builder.UseSetting("Notifications:ContainerStopWait", "00:00:00.1"),
        };
        await AddRuleForEverythingAsync(app);
        await using var agent = await app.EnrollAndConnectAsync();

        // A requested stop first: Docker reports it as stop, then die, and that is not a crash.
        await agent.SendAsync(new AgentMessage { ContainerEvent = new ContainerEvent { ContainerId = "c-stopped", Action = "stop" } });
        await agent.SendAsync(Died("c-stopped", "Exited (143) 1 second ago"));
        await agent.SendAsync(Died("c-crashed", "Exited (2) 1 second ago"));

        using var call = app.BotGateway.WatchNotifications(
            new WatchNotificationsRequest(), new Metadata { { "x-bot-token", AggregatorApp.BotToken } }, cancellationToken: Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        Assert.True(await call.ResponseStream.MoveNext(timeout.Token));
        var notification = call.ResponseStream.Current;

        Assert.Equal(NotificationKind.ContainerDied, notification.Kind);
        Assert.Equal(BotNotification.DetailsOneofCase.Container, notification.DetailsCase);
        Assert.Equal("c-crashed", notification.Container.ContainerId);
        Assert.Equal("shop-db-1", notification.Container.ContainerName);
        Assert.Equal("shop", notification.Container.StackName);
        Assert.Equal("db", notification.Container.ServiceName);
        Assert.Equal(2, notification.Container.ExitCode);
        Assert.False(notification.Container.System);
        Assert.Single(await ListAsync(app));
    }

    [Fact]
    public async Task Container_killed_for_memory_keeps_its_details_through_the_database()
    {
        await using var app = new AggregatorApp
        {
            Customize = builder => builder.UseSetting("Notifications:ContainerStopWait", "00:00:00.1"),
        };
        await AddRuleForEverythingAsync(app);
        await using var agent = await app.EnrollAndConnectAsync();

        // The kernel's OOM killer: Docker reports oom, then die with 137 (SIGKILL).
        await agent.SendAsync(new AgentMessage { ContainerEvent = new ContainerEvent { ContainerId = "c-oom", Action = "oom" } });
        // A system container (the agent's ContainerInfo.system): the bot offers it no Restart button.
        await agent.SendAsync(Died("c-oom", "Exited (137) 1 second ago", system: true));

        var notification = Assert.Single(await WaitForAsync(app, 1));
        Assert.StartsWith("Container 'shop-db-1' of stack 'shop' on ", notification.Summary);
        Assert.EndsWith(" was killed: out of memory.", notification.Summary);
        var details = Assert.IsType<ContainerNotificationDetails>(notification.Details);
        Assert.True(details.OutOfMemory);
        Assert.True(details.IsSystem);
    }

    [Fact]
    public async Task Exit_code_of_the_event_survives_a_restart_policy()
    {
        await using var app = new AggregatorApp
        {
            Customize = builder => builder.UseSetting("Notifications:ContainerStopWait", "00:00:00.1"),
        };
        await AddRuleForEverythingAsync(app);
        await using var agent = await app.EnrollAndConnectAsync();

        // restart: unless-stopped: by the time the agent looks the container up, Docker has started it again.
        var died = Died("c-restarted", "Up Less than a second");
        died.ContainerEvent.ExitCode = 3;
        await agent.SendAsync(died);

        var notification = Assert.Single(await WaitForAsync(app, 1));
        Assert.EndsWith(" exited with code 3.", notification.Summary);
        Assert.Equal(3, Assert.IsType<ContainerNotificationDetails>(notification.Details).ExitCode);
    }

    private static AgentMessage Died(string containerId, string status, bool system = false) => new()
    {
        ContainerEvent = new ContainerEvent
        {
            ContainerId = containerId,
            Action = "die",
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Container = new ContainerInfo
            {
                Id = containerId,
                Name = "shop-db-1",
                State = ContainerForest.Contracts.Agent.ContainerState.Exited,
                StackName = "shop",
                ServiceName = "db",
                StatusText = status,
                System = system,
            },
        },
    };

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
