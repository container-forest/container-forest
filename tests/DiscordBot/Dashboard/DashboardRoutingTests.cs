extern alias bot;

using System.Reflection;
using bot::ContainerForest.DiscordBot.Dashboard;
using bot::ContainerForest.DiscordBot.Forest;
using bot::ContainerForest.DiscordBot.Interactions;
using ContainerForest.DiscordBot.Tests.Commands;
using Discord;
using Discord.Interactions;
using Discord.Rest;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ContainerForest.DiscordBot.Tests.Dashboard;

/// <summary>
/// Discord.Net routes a click by the button's custom id. These tests ask its own router whether every id the
/// dashboard draws reaches <see cref="DashboardModule"/>, so a change of prefix or wildcard cannot break clicks unseen.
/// </summary>
public sealed class DashboardRoutingTests : IAsyncLifetime
{
    private static readonly Guid Environment = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid Stack = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c");

    private readonly InteractionService interactions = new(new DiscordRestClient());

    public static TheoryData<DashboardRoute> Routes =>
    [
        new EnvironmentsRoute(1),
        new StacksRoute(Environment, 0),
        new StackRoute(Environment, Stack, 2),
        new ConfirmRoute(StackOperation.Down, Environment, Stack, 0),
        new RunRoute(StackOperation.Redeploy, Environment, Stack, 0),
        new DeploymentRoute(Environment, Stack, Guid.NewGuid(), 0),
    ];

    public async ValueTask InitializeAsync() =>
        await interactions.AddModuleAsync<DashboardModule>(new ServiceCollection()
            .AddLogging()
            .AddSingleton<IForestClient>(new StubForestClient())
            .AddSingleton(TimeProvider.System)
            .AddSingleton<DashboardService>()
            .AddSingleton<IHostApplicationLifetime, StoppedNever>()
            .BuildServiceProvider());

    public ValueTask DisposeAsync()
    {
        interactions.Dispose();
        return ValueTask.CompletedTask;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void EveryRoute_ReachesTheDashboard_WithTheWholeRestOfTheId(DashboardRoute route)
    {
        var id = route.ToCustomId();

        var result = interactions.SearchComponentCommand(ComponentClick.With(id));

        Assert.True(result.IsSuccess, result.ErrorReason);
        Assert.Equal(nameof(DashboardModule.NavigateAsync), result.Command.MethodName);
        Assert.Equal(id[(DashboardRoute.Prefix.Length + 1)..], Assert.Single(result.RegexCaptureGroups));
    }

    [Fact]
    public void ForeignIds_AreNotTheDashboards()
    {
        Assert.False(interactions.SearchComponentCommand(ComponentClick.With("other:envs:0")).IsSuccess);
    }
}

/// <summary>A component interaction carrying only a custom id: all the router reads.</summary>
public class ComponentClick : DispatchProxy
{
    private string customId = "";

    private IComponentInteractionData? data;

    public static IComponentInteraction With(string customId)
    {
        var clickData = Create<IComponentInteractionData, ComponentClick>();
        ((ComponentClick)(object)clickData).customId = customId;
        var interaction = Create<IComponentInteraction, ComponentClick>();
        ((ComponentClick)(object)interaction).data = clickData;
        return interaction;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "get_CustomId" => customId,
        "get_Data" => data,
        "get_Type" => InteractionType.MessageComponent,
        "get_Values" => Array.Empty<string>(),
        _ => targetMethod?.ReturnType is { IsValueType: true } type && type != typeof(void) ? Activator.CreateInstance(type) : null,
    };
}

/// <summary>A host that never stops, for building the module.</summary>
internal sealed class StoppedNever : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication()
    {
    }
}
