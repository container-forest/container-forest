extern alias bot;

using bot::ContainerForest.DiscordBot.Commands;
using bot::ContainerForest.DiscordBot.Dashboard;
using bot::ContainerForest.DiscordBot.Forest;
using bot::ContainerForest.DiscordBot.Interactions;
using bot::ContainerForest.DiscordBot.Notifications;
using ContainerForest.DiscordBot.Tests.Commands;
using ContainerForest.DiscordBot.Tests.Dashboard;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ContainerForest.DiscordBot.Tests.Notifications;

/// <summary>
/// The notification buttons and /dashboard share Discord.Net's router: each click must reach its own module, so a
/// change of prefix or wildcard cannot send a Restart click to the dashboard or the other way round.
/// </summary>
public sealed class NotificationRoutingTests : IAsyncLifetime
{
    private static readonly Guid Environment = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    private readonly InteractionService interactions = new(new DiscordRestClient());

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IForestClient>(new StubForestClient())
            .AddSingleton(TimeProvider.System)
            .AddSingleton<EnvironmentResolver>()
            .AddSingleton<RestartContainerCommand>()
            .AddSingleton<DashboardService>()
            .AddSingleton<IHostApplicationLifetime, StoppedNever>()
            .BuildServiceProvider();
        await interactions.AddModuleAsync<NotificationModule>(services);
        await interactions.AddModuleAsync<DashboardModule>(services);
    }

    public ValueTask DisposeAsync()
    {
        interactions.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void RestartButton_ReachesTheNotificationModule_WithTheWholeRestOfTheId()
    {
        var id = new RestartContainerButton(Environment, "web-app-1").ToCustomId();

        var result = interactions.SearchComponentCommand(ComponentClick.With(id));

        Assert.True(result.IsSuccess, result.ErrorReason);
        Assert.Equal(nameof(NotificationModule.RestartAsync), result.Command.MethodName);
        Assert.Equal(id[(RestartContainerButton.Prefix.Length + 1)..], Assert.Single(result.RegexCaptureGroups));
    }

    [Fact]
    public void DashboardButtons_StillReachTheDashboard()
    {
        var result = interactions.SearchComponentCommand(ComponentClick.With(new EnvironmentsRoute(0).ToCustomId()));

        Assert.True(result.IsSuccess, result.ErrorReason);
        Assert.Equal(nameof(DashboardModule.NavigateAsync), result.Command.MethodName);
    }
}
