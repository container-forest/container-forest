extern alias bot;

using bot::ContainerForest.DiscordBot.Commands;
using bot::ContainerForest.DiscordBot.Dashboard;
using bot::ContainerForest.DiscordBot.Forest;
using ContainerForest.DiscordBot.Tests.Commands;
using ContainerForest.DiscordBot.Tests.Forest;
using Microsoft.Extensions.Logging;

namespace ContainerForest.DiscordBot.Tests.Dashboard;

public sealed class DashboardServiceTests
{
    private const string EnvironmentId = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";
    private const string StackId = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly BotUser User = RecordingInteraction.Dmytro;
    private static readonly Guid Environment = Guid.Parse(EnvironmentId);
    private static readonly Guid Stack = Guid.Parse(StackId);

    private readonly StubForestClient forest = new();
    private readonly RecordingLogger<DashboardService> logger = new();
    private readonly List<DashboardScreen> progress = [];
    private readonly InstantTimeProvider time = new();

    public DashboardServiceTests()
    {
        forest.Environments.Add(new ForestEnvironment(EnvironmentId, "Server-R2", Online: true));
        forest.Stacks[EnvironmentId] =
        [
            new ForestStack(StackId, "kokosik-prod", IsSystem: false, Sha, DateTimeOffset.FromUnixTimeSeconds(1_790_973_600),
                CanRestart: true, CanTakeDown: true, CanDeploy: true),
        ];
        forest.Containers[EnvironmentId] =
        [
            Container("kokosik-prod-web-1", "kokosik-prod", ContainerState.Running),
            Container("kokosik-prod-db-1", "kokosik-prod", ContainerState.Exited),
            Container("watchtower", stack: null, ContainerState.Running),
        ];
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DashboardService Service => new(forest, time, logger);

    [Fact]
    public async Task Environments_AreSortedByName_AndPaged_AndAPageTooFarShowsTheLast()
    {
        forest.Environments.Clear();
        forest.Environments.AddRange(Enumerable.Range(1, 7).Reverse()
            .Select(i => new ForestEnvironment(Guid.NewGuid().ToString(), $"env-{i}", Online: i % 2 == 0)));

        var first = Assert.IsType<EnvironmentsScreen>(await ShowAsync(new EnvironmentsRoute(0)));
        var beyond = Assert.IsType<EnvironmentsScreen>(await ShowAsync(new EnvironmentsRoute(9)));

        Assert.Equal(["env-1", "env-2", "env-3", "env-4", "env-5"], first.Environments.Items.Select(e => e.Name));
        Assert.Equal((0, 2, 7, false, true), (first.Environments.Number, first.Environments.Count, first.Environments.Total,
            first.Environments.HasPrevious, first.Environments.HasNext));
        Assert.Equal(["env-6", "env-7"], beyond.Environments.Items.Select(e => e.Name));
        Assert.Equal(1, beyond.Environments.Number);
    }

    [Fact]
    public async Task Stacks_CountTheirOwnContainers()
    {
        var screen = Assert.IsType<StacksScreen>(await ShowAsync(new StacksRoute(Environment, 0)));

        var row = Assert.Single(screen.Stacks.Items);
        Assert.Equal("kokosik-prod", row.Stack.Name);
        Assert.Equal(new ContainerCount(Running: 1, Total: 2), row.Containers);
    }

    [Fact]
    public async Task Stacks_OfAnOfflineEnvironment_DoNotAskTheAgent()
    {
        forest.Environments[0] = forest.Environments[0] with { Online = false };

        var screen = Assert.IsType<StacksScreen>(await ShowAsync(new StacksRoute(Environment, 0)));

        Assert.Null(Assert.Single(screen.Stacks.Items).Containers);
        Assert.DoesNotContain(forest.Calls, c => c.StartsWith("containers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stack_ShowsItsContainers_AndWhatTheUserMayDo()
    {
        var screen = Assert.IsType<StackScreen>(await ShowAsync(new StackRoute(Environment, Stack, StacksPage: 3)));

        Assert.Equal(["kokosik-prod-db-1", "kokosik-prod-web-1"], screen.Containers!.Select(c => c.Name));
        Assert.Equal(
            [StackOperation.Redeploy, StackOperation.RedeployWithPull, StackOperation.Restart, StackOperation.Down],
            screen.Operations);
        Assert.Equal(3, screen.StacksPage);
        Assert.True(screen.CanAct);
    }

    [Fact]
    public async Task Stack_ThatIsGone_IsAnError_WithAWayBackToTheStacks()
    {
        var screen = Assert.IsType<ErrorScreen>(await ShowAsync(new StackRoute(Environment, Guid.NewGuid(), StacksPage: 2)));

        Assert.Contains("stack is gone", screen.Message, StringComparison.Ordinal);
        Assert.Equal(new StacksRoute(Environment, 2), screen.Back);
    }

    [Fact]
    public async Task UnlinkedAccount_IsAnError_WithNowhereToGo()
    {
        forest.Failure = new AccountNotLinkedException();

        var screen = Assert.IsType<ErrorScreen>(await ShowAsync(new EnvironmentsRoute(0)));

        Assert.Contains("/link", screen.Message, StringComparison.Ordinal);
        Assert.Null(screen.Back);
    }

    [Fact]
    public async Task UnexpectedFailure_IsLogged_AndShownAsAGenericError()
    {
        forest.Failure = new InvalidOperationException("boom");

        var screen = Assert.IsType<ErrorScreen>(await ShowAsync(new StacksRoute(Environment, 0)));

        Assert.Equal(BotCommand<NoArgs, NoArgs>.UnexpectedErrorMessage, screen.Message);
        Assert.Equal(new EnvironmentsRoute(0), screen.Back);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirm_NamesTheOperation_WithoutRunningIt()
    {
        var screen = Assert.IsType<ConfirmScreen>(await ShowAsync(new ConfirmRoute(StackOperation.Down, Environment, Stack, 0)));

        Assert.Equal((StackOperation.Down, "kokosik-prod"), (screen.Operation, screen.Stack.Name));
        Assert.DoesNotContain(forest.Calls, c => c.StartsWith("stack-down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_ShowsProgressFirst_ThenTheResultWithFreshData()
    {
        forest.StackResult = new ForestStackResult(Error: null, ["Container kokosik-prod-web-1 Restarting"]);

        var screen = Assert.IsType<StackScreen>(await ShowAsync(new RunRoute(StackOperation.Restart, Environment, Stack, 0)));

        var shown = Assert.IsType<StackScreen>(Assert.Single(progress));
        Assert.True(shown.Busy);
        Assert.False(shown.CanAct);
        Assert.Equal(NoticeKind.Progress, shown.Notice!.Kind);

        Assert.Contains($"stack-restart {User.Id} {EnvironmentId} kokosik-prod default", forest.Calls);
        Assert.False(screen.Busy);
        Assert.Equal((NoticeKind.Success, "Restarted kokosik-prod."), (screen.Notice!.Kind, screen.Notice.Text));
        Assert.Equal(["Container kokosik-prod-web-1 Restarting"], screen.Notice.Output);

        // The stack is loaded again after the action, so the screen shows the containers as they are now.
        Assert.Equal(2, forest.Calls.Count(c => c.StartsWith("stacks", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FailedDown_ShowsComposesError_AndOutput()
    {
        forest.StackResult = new ForestStackResult("docker compose down exited with code 1", ["Error response from daemon"]);

        var screen = Assert.IsType<StackScreen>(await ShowAsync(new RunRoute(StackOperation.Down, Environment, Stack, 0)));

        Assert.Contains($"stack-down {User.Id} {EnvironmentId} kokosik-prod default", forest.Calls);
        Assert.Equal(NoticeKind.Failure, screen.Notice!.Kind);
        Assert.Equal("docker compose down exited with code 1", screen.Notice.Text);
        Assert.Equal(["Error response from daemon"], screen.Notice.Output);
    }

    [Theory]
    [InlineData(StackOperation.Redeploy, "missing")]
    [InlineData(StackOperation.RedeployWithPull, "pull")]
    public async Task Redeploy_QueuesTheDeployment_AndUpdatesTheSameMessageUntilItEnds(StackOperation operation, string pull)
    {
        forest.DeploymentSequence.Enqueue(Deployment(DeploymentStatus.Queued));
        forest.DeploymentSequence.Enqueue(Deployment(DeploymentStatus.Queued));
        forest.DeploymentSequence.Enqueue(Deployment(DeploymentStatus.Running));
        forest.DeploymentSequence.Enqueue(Deployment(DeploymentStatus.Running));
        forest.Deployment = Deployment(DeploymentStatus.Succeeded);

        var screen = Assert.IsType<StackScreen>(await ShowAsync(new RunRoute(operation, Environment, Stack, 0)));

        Assert.Contains($"deploy {User.Id} {EnvironmentId} kokosik-prod {pull}", forest.Calls);
        Assert.Equal(5, forest.Calls.Count(c => c == $"deployment {User.Id} {forest.DeploymentId}"));

        // One edit per change of status, not per poll; every button stays disabled meanwhile.
        Assert.Equal(["Deployment queued…", "Deploying commit 0123456…"], progress.Cast<StackScreen>().Select(s => s.Notice!.Text));
        Assert.All(progress.Cast<StackScreen>(), s => Assert.True(s.Busy));
        Assert.Equal(4 * DashboardService.FollowInterval, time.Elapsed);

        Assert.False(screen.Busy);
        Assert.Equal(Guid.Parse(forest.DeploymentId), screen.DeploymentId);
        Assert.Equal(new Notice(NoticeKind.Success, "Deployed commit 0123456."), screen.Notice);

        // The finished deployment changed the stack: it is loaded again for the final screen.
        Assert.Equal(2, forest.Calls.Count(c => c.StartsWith("stacks", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task LongDeployment_IsFollowedOnlyWhileDiscordLetsTheMessageBeEdited()
    {
        forest.Deployment = Deployment(DeploymentStatus.Running);

        var screen = Assert.IsType<StackScreen>(await ShowAsync(new RunRoute(StackOperation.Redeploy, Environment, Stack, 0)));

        Assert.Equal(DashboardService.FollowLimit, time.Elapsed);
        Assert.Single(progress);
        Assert.False(screen.Busy);
        Assert.Equal(NoticeKind.Progress, screen.Notice!.Kind);
        Assert.EndsWith("press Refresh to check again.", screen.Notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_OfAFinishedDeployment_ShowsTheResultAtOnce()
    {
        forest.Deployment = Deployment(DeploymentStatus.Succeeded);

        var screen = Assert.IsType<StackScreen>(await ShowAsync(new DeploymentRoute(Environment, Stack, Guid.Parse(forest.DeploymentId), 0)));

        Assert.Empty(progress);
        Assert.Equal(TimeSpan.Zero, time.Elapsed);
        Assert.Equal(NoticeKind.Success, screen.Notice!.Kind);
    }

    [Fact]
    public async Task Refresh_OfARunningDeployment_FollowsItAgain()
    {
        forest.DeploymentSequence.Enqueue(Deployment(DeploymentStatus.Running));
        forest.Deployment = Deployment(DeploymentStatus.Failed) with { Error = "compose failed" };

        var screen = Assert.IsType<StackScreen>(await ShowAsync(new DeploymentRoute(Environment, Stack, Guid.Parse(forest.DeploymentId), 0)));

        Assert.True(Assert.IsType<StackScreen>(Assert.Single(progress)).Busy);
        Assert.Equal(new Notice(NoticeKind.Failure, "Deployment failed: compose failed"), screen.Notice);
    }

    [Fact]
    public async Task Deployment_ShowsHowItEnded()
    {
        forest.Deployment = new ForestDeployment(
            DeploymentStatus.Failed, Sha, "Directory 'missing' does not exist at commit 0123456.", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var deployment = Guid.Parse(forest.DeploymentId);

        var screen = Assert.IsType<StackScreen>(await ShowAsync(new DeploymentRoute(Environment, Stack, deployment, 1)));

        Assert.Equal(deployment, screen.DeploymentId);
        Assert.Equal(
            new Notice(NoticeKind.Failure, "Deployment failed: Directory 'missing' does not exist at commit 0123456."),
            screen.Notice);
    }

    [Theory]
    [InlineData(DeploymentStatus.Queued, NoticeKind.Progress, "Deployment queued")]
    [InlineData(DeploymentStatus.Running, NoticeKind.Progress, "Deploying commit 0123456")]
    [InlineData(DeploymentStatus.Succeeded, NoticeKind.Success, "Deployed commit 0123456.")]
    [InlineData(DeploymentStatus.Cancelled, NoticeKind.Failure, "cancelled")]
    [InlineData(DeploymentStatus.Unknown, NoticeKind.Info, "check the panel")]
    public void DescribeDeployment_SaysWhereItIs(DeploymentStatus status, NoticeKind kind, string text)
    {
        var notice = DashboardService.DescribeDeployment(new ForestDeployment(status, Sha, null, DateTimeOffset.UnixEpoch, null));

        Assert.Equal(kind, notice.Kind);
        Assert.Contains(text, notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewerOfAStack_GetsNoOperations()
    {
        forest.Stacks[EnvironmentId][0] = forest.Stacks[EnvironmentId][0] with { CanRestart = false, CanTakeDown = false, CanDeploy = false };

        var screen = Assert.IsType<StackScreen>(await ShowAsync(new StackRoute(Environment, Stack, 0)));

        Assert.Empty(screen.Operations);
    }

    private Task<DashboardScreen> ShowAsync(DashboardRoute route) =>
        Service.ShowAsync(User, route, s => { progress.Add(s); return Task.CompletedTask; }, Ct);

    private static ForestDeployment Deployment(DeploymentStatus status) =>
        new(status, status == DeploymentStatus.Queued ? null : Sha, null, DateTimeOffset.UnixEpoch,
            status is DeploymentStatus.Succeeded or DeploymentStatus.Failed ? DateTimeOffset.UnixEpoch : null);

    private static ForestContainer Container(string name, string? stack, ContainerState state) =>
        new(name + "-id", name, state, "nginx:1.27", stack, "web", state == ContainerState.Running ? "Up 3 minutes" : "Exited (1)", CanControl: true);
}
