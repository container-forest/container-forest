extern alias bot;

using bot::ContainerForest.DiscordBot.Dashboard;
using bot::ContainerForest.DiscordBot.Forest;
using bot::ContainerForest.DiscordBot.Interactions;
using Discord;

namespace ContainerForest.DiscordBot.Tests.Dashboard;

public sealed class DashboardRendererTests
{
    private const string EnvironmentId = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>Discord's limit on components in one message, nested ones included.</summary>
    private const int MaxComponents = 40;

    private static readonly ForestEnvironment Online = new(EnvironmentId, "Server-R2", Online: true);
    private static readonly Guid Environment = Guid.Parse(EnvironmentId);

    [Fact]
    public void FullPageOfStacks_StaysWithinDiscordsLimits()
    {
        var rows = Enumerable.Range(1, 12)
            .Select(i => new StackRow(StackNamed($"stack-{i:00}-with-a-rather-long-name-for-the-layout"), new ContainerCount(2, 3)))
            .ToList();
        var screen = new StacksScreen(Online, PageOf<StackRow>.Of(rows, 1, DashboardService.PageSize));

        var message = DashboardRenderer.Render(screen);

        AssertWithinLimits(message);
        Assert.Equal(
            ["Back", "Next", "Refresh · 2/3", "Environments"],
            Buttons(message).Where(b => b.Label != "Details").Select(b => b.Label));
        Assert.Equal(5, Buttons(message).Count(b => b.Label == "Details"));
        Assert.All(Buttons(message), b => Assert.False(b.IsDisabled));
    }

    [Fact]
    public void FirstAndOnlyPage_DisablesPaging_WithIdsThatStillDiffer()
    {
        var screen = new EnvironmentsScreen(PageOf<ForestEnvironment>.Of([Online], 0, DashboardService.PageSize));

        var message = DashboardRenderer.Render(screen);

        AssertWithinLimits(message);
        var buttons = Buttons(message);
        Assert.True(buttons.Single(b => b.Label == "Back").IsDisabled);
        Assert.True(buttons.Single(b => b.Label == "Next").IsDisabled);
        var stacks = buttons.Single(b => b.Label == "Stacks");
        Assert.True(DashboardRoute.TryParse(stacks.CustomId, out var route));
        Assert.Equal(new StacksRoute(Environment, 0), route);
        Assert.Contains("🟢 Online", Texts(message), StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_SeesEveryOperation_DownAndRedeployAskFirst_RestartRunsAtOnce()
    {
        var screen = new StackScreen(Online, StackNamed("web", canDeploy: true, canRestart: true, canTakeDown: true), [], StacksPage: 2);

        var buttons = Buttons(DashboardRenderer.Render(screen));

        Assert.Equal(
            ["Redeploy", "Redeploy + pull", "Restart", "Down", "Back", "Refresh"],
            buttons.Select(b => b.Label));
        var routes = buttons.ToDictionary(b => b.Label, b => Route(b.CustomId));
        var stack = screen.StackGuid;
        Assert.Equal(new ConfirmRoute(StackOperation.Redeploy, Environment, stack, 2), routes["Redeploy"]);
        Assert.Equal(new ConfirmRoute(StackOperation.RedeployWithPull, Environment, stack, 2), routes["Redeploy + pull"]);
        Assert.Equal(new RunRoute(StackOperation.Restart, Environment, stack, 2), routes["Restart"]);
        Assert.Equal(new ConfirmRoute(StackOperation.Down, Environment, stack, 2), routes["Down"]);
        Assert.Equal(new StacksRoute(Environment, 2), routes["Back"]);
        Assert.Equal(ButtonStyle.Danger, buttons.Single(b => b.Label == "Down").Style);
    }

    [Fact]
    public void Viewer_SeesNoOperations_AndIsToldSo()
    {
        var screen = new StackScreen(Online, StackNamed("web"), [], StacksPage: 0);

        var message = DashboardRenderer.Render(screen);

        Assert.Equal(["Back", "Refresh"], Buttons(message).Select(b => b.Label));
        Assert.Contains("You can only view this stack.", Texts(message), StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineAgent_DisablesOperations_ButNotNavigation()
    {
        var screen = new StackScreen(Online with { Online = false }, StackNamed("web", canRestart: true), null, StacksPage: 0);

        var buttons = Buttons(DashboardRenderer.Render(screen));

        Assert.True(buttons.Single(b => b.Label == "Restart").IsDisabled);
        Assert.False(buttons.Single(b => b.Label == "Back").IsDisabled);
    }

    [Fact]
    public void BusyStack_DisablesEveryButton()
    {
        var screen = new StackScreen(Online, StackNamed("web", canRestart: true), [], 0, new Notice(NoticeKind.Progress, "Restarting web…"), Busy: true);

        Assert.All(Buttons(DashboardRenderer.Render(screen)), b => Assert.True(b.IsDisabled));
    }

    [Fact]
    public void FollowedDeployment_IsRefreshedByItsOwnRoute()
    {
        var deployment = Guid.NewGuid();
        var screen = new StackScreen(Online, StackNamed("web"), [], 4, new Notice(NoticeKind.Progress, "Deployment queued."), deployment);

        var refresh = Buttons(DashboardRenderer.Render(screen)).Single(b => b.Label == "Refresh");

        Assert.Equal(new DeploymentRoute(Environment, screen.StackGuid, deployment, 4), Route(refresh.CustomId));
    }

    [Fact]
    public void LongOutputAndManyContainers_StayWithinTheTextLimit_AndKeepTheLastLine()
    {
        var containers = Enumerable.Range(1, 25)
            .Select(i => new ForestContainer($"id-{i}", $"web-app-{i}-with-a-long-container-name", ContainerState.Running,
                "nginx:1.27", "web", "app", "Up 3 hours (healthy) and a long Docker status text", CanControl: true))
            .ToList();
        var output = Enumerable.Range(1, 400).Select(i => $"Container web-app-{i} line {i} " + new string('x', 60)).ToList();
        var screen = new StackScreen(
            Online, StackNamed("web", canRestart: true), containers, 0, new Notice(NoticeKind.Failure, "compose failed", output));

        var message = DashboardRenderer.Render(screen);

        AssertWithinLimits(message);
        var text = Texts(message);
        Assert.Contains("Container web-app-400 line 400", text, StringComparison.Ordinal);
        Assert.Contains("…and 15 more.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesAreEscaped_SoTheyCannotFormatTheMessage()
    {
        var screen = new StacksScreen(
            Online with { Name = "prod_eu" },
            PageOf<StackRow>.Of([new StackRow(StackNamed("**web**"), null)], 0, DashboardService.PageSize));

        var text = Texts(DashboardRenderer.Render(screen));

        Assert.Contains(@"## \*\*web\*\*", text, StringComparison.Ordinal);
        Assert.Contains("unknown while the agent is unavailable", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirm_RunsOrCancels()
    {
        var screen = new ConfirmScreen(Online, StackNamed("web", canTakeDown: true), StackOperation.Down, 1);

        var message = DashboardRenderer.Render(screen);

        var buttons = Buttons(message);
        Assert.Equal(["Yes, down", "Cancel"], buttons.Select(b => b.Label));
        Assert.Equal(new RunRoute(StackOperation.Down, Environment, screen.StackGuid, 1), Route(buttons[0].CustomId));
        Assert.Equal(new StackRoute(Environment, screen.StackGuid, 1), Route(buttons[1].CustomId));
        Assert.Contains("Volumes and files stay", Texts(message), StringComparison.Ordinal);
    }

    [Fact]
    public void Error_OffersAWayBack_OnlyWhenThereIsOne()
    {
        var withBack = DashboardRenderer.Render(new ErrorScreen("Not *found*.", new EnvironmentsRoute(0)));
        var without = DashboardRenderer.Render(new ErrorScreen("Link your account.", null));

        Assert.Equal(["Back"], Buttons(withBack).Select(b => b.Label));
        Assert.Contains(@"❌ Not \*found\*.", Texts(withBack), StringComparison.Ordinal);
        Assert.Empty(Buttons(without));
    }

    private static ForestStack StackNamed(string name, bool canDeploy = false, bool canRestart = false, bool canTakeDown = false) =>
        new(Guid.NewGuid().ToString(), name, IsSystem: false, Sha, DateTimeOffset.FromUnixTimeSeconds(1_790_973_600),
            canRestart, canTakeDown, canDeploy);

    private static DashboardRoute Route(string customId) =>
        DashboardRoute.TryParse(customId, out var route) ? route : throw new InvalidOperationException($"'{customId}' is not a route.");

    private static void AssertWithinLimits(MessageComponent message)
    {
        var all = Flatten(message.Components).ToList();
        Assert.True(all.Count <= MaxComponents, $"{all.Count} components");
        Assert.True(Texts(message).Length <= DashboardRenderer.MaxTextLength, $"{Texts(message).Length} characters of text");
        var ids = Buttons(message).Select(b => b.CustomId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id.Length <= DashboardRoute.MaxCustomIdLength, id));
    }

    private static List<ButtonComponent> Buttons(MessageComponent message) =>
        [.. Flatten(message.Components).OfType<ButtonComponent>()];

    private static string Texts(MessageComponent message) =>
        string.Concat(Flatten(message.Components).OfType<TextDisplayComponent>().Select(t => t.Content));

    private static IEnumerable<IMessageComponent> Flatten(IEnumerable<IMessageComponent> components)
    {
        foreach (var component in components)
        {
            yield return component;
            var children = component switch
            {
                ContainerComponent c => c.Components,
                ActionRowComponent r => r.Components,
                SectionComponent s => [.. s.Components, s.Accessory],
                _ => [],
            };
            foreach (var child in Flatten(children))
            {
                yield return child;
            }
        }
    }
}
