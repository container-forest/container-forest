using ContainerForest.DiscordBot.Commands;
using ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Dashboard;

/// <summary>
/// /dashboard without Discord: turns a route into the screen to show, calling the aggregator as the user who clicked.
/// Every screen loads fresh data, so a click always shows the current state; the aggregator decides what the user
/// may see and do (security invariant 4), the buttons only follow its can_* hints. Everything happens in one
/// message: an operation that takes a while edits it as it goes instead of posting follow-ups.
/// </summary>
public sealed class DashboardService(IForestClient forest, TimeProvider time, ILogger<DashboardService> logger)
{
    /// <summary>
    /// Rows per page. A message holds at most 40 components and each row takes four (section, text, button,
    /// separator), so five leave room for the header and the navigation.
    /// </summary>
    public const int PageSize = 5;

    /// <summary>Containers listed on the stack screen; the rest are only counted, to stay within the message's text limit.</summary>
    public const int MaxContainerLines = 10;

    /// <summary>How often a running deployment is asked about again.</summary>
    public static readonly TimeSpan FollowInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a deployment is followed. Discord lets the bot edit the message for 15 minutes after the click, and
    /// a deployment that takes longer than this is shown as still running, with Refresh to follow it again.
    /// </summary>
    public static readonly TimeSpan FollowLimit = TimeSpan.FromMinutes(13);

    /// <summary>
    /// The screen for <paramref name="route"/>. While an operation runs (compose for restart and down, a deployment
    /// for redeploy), <paramref name="showProgress"/> gets the stack with every action disabled and how it is going.
    /// </summary>
    public async Task<DashboardScreen> ShowAsync(
        BotUser user, DashboardRoute route, Func<DashboardScreen, Task> showProgress, CancellationToken cancellationToken)
    {
        try
        {
            return route switch
            {
                EnvironmentsRoute r => await EnvironmentsAsync(user, r.Page, cancellationToken),
                StacksRoute r => await StacksAsync(user, r.EnvironmentId, r.Page, cancellationToken),
                StackRoute r => await StackAsync(user, r.EnvironmentId, r.StackId, r.StacksPage, cancellationToken),
                ConfirmRoute r => await ConfirmAsync(user, r, cancellationToken),
                RunRoute r => await RunAsync(user, r, showProgress, cancellationToken),
                DeploymentRoute r => await FollowDeploymentAsync(user, r, showProgress, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
            };
        }
        catch (ForestException ex)
        {
            return new ErrorScreen(ex.Message, ex is AccountNotLinkedException ? null : BackFrom(route));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "/dashboard failed on {Route} for Discord user {DiscordUserId}", route, user.Id);
            return new ErrorScreen(BotCommand<NoArgs, NoArgs>.UnexpectedErrorMessage, BackFrom(route));
        }
    }

    /// <summary>Where "Back" leads from a screen that failed: one level up, so the user is never stuck.</summary>
    internal static DashboardRoute BackFrom(DashboardRoute route) => route switch
    {
        StackRoute r => new StacksRoute(r.EnvironmentId, r.StacksPage),
        ConfirmRoute r => new StackRoute(r.EnvironmentId, r.StackId, r.StacksPage),
        RunRoute r => new StackRoute(r.EnvironmentId, r.StackId, r.StacksPage),
        DeploymentRoute r => new StackRoute(r.EnvironmentId, r.StackId, r.StacksPage),
        _ => new EnvironmentsRoute(0),
    };

    private async Task<EnvironmentsScreen> EnvironmentsAsync(BotUser user, int page, CancellationToken cancellationToken)
    {
        var environments = (await forest.ListEnvironmentsAsync(user.Id, cancellationToken))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new EnvironmentsScreen(PageOf<ForestEnvironment>.Of(environments, page, PageSize));
    }

    private async Task<StacksScreen> StacksAsync(BotUser user, Guid environmentId, int page, CancellationToken cancellationToken)
    {
        var environment = await FindEnvironmentAsync(user, environmentId, cancellationToken);
        var stacks = (await forest.ListStacksAsync(user.Id, environment.Id, cancellationToken))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        var containers = await TryListContainersAsync(user, environment, cancellationToken);

        var rows = stacks
            .Select(s => new StackRow(s, containers is null ? null : ContainerCount.Of(containers.Where(c => c.StackName == s.Name))))
            .ToList();
        return new StacksScreen(environment, PageOf<StackRow>.Of(rows, page, PageSize));
    }

    private async Task<StackScreen> StackAsync(
        BotUser user, Guid environmentId, Guid stackId, int stacksPage, CancellationToken cancellationToken)
    {
        var environment = await FindEnvironmentAsync(user, environmentId, cancellationToken);
        var stack = await FindStackAsync(user, environment, stackId, cancellationToken);
        var containers = await TryListContainersAsync(user, environment, cancellationToken);
        return new StackScreen(
            environment,
            stack,
            containers?.Where(c => c.StackName == stack.Name).OrderBy(c => c.Name, StringComparer.Ordinal).ToList(),
            stacksPage);
    }

    private async Task<DashboardScreen> ConfirmAsync(BotUser user, ConfirmRoute route, CancellationToken cancellationToken)
    {
        var screen = await StackAsync(user, route.EnvironmentId, route.StackId, route.StacksPage, cancellationToken);
        return new ConfirmScreen(screen.Environment, screen.Stack, route.Operation, route.StacksPage);
    }

    private async Task<StackScreen> RunAsync(
        BotUser user, RunRoute route, Func<DashboardScreen, Task> showProgress, CancellationToken cancellationToken)
    {
        var before = await StackAsync(user, route.EnvironmentId, route.StackId, route.StacksPage, cancellationToken);
        var stack = before.Stack.Name;

        if (route.Operation.IsQueued())
        {
            var pull = route.Operation == StackOperation.RedeployWithPull;
            var deploymentId = await forest.DeployStackAsync(user.Id, before.Environment.Id, stack, pull, cancellationToken);
            return await FollowAsync(user, before, Guid.Parse(deploymentId), showProgress, cancellationToken);
        }

        // Compose can take minutes; the user sees that it runs instead of a frozen message.
        var action = route.Operation == StackOperation.Down ? StackAction.Down : StackAction.Restart;
        var running = action == StackAction.Down ? "Taking down" : "Restarting";
        await showProgress(before with
        {
            Notice = new Notice(NoticeKind.Progress, $"{running} {stack}… this can take a few minutes."),
            Busy = true,
        });

        var result = await forest.ControlStackAsync(user.Id, before.Environment.Id, stack, action, timeoutSeconds: null, cancellationToken);
        var notice = result.Succeeded
            ? new Notice(NoticeKind.Success, $"{(action == StackAction.Down ? "Took down" : "Restarted")} {stack}.", result.Output)
            : new Notice(NoticeKind.Failure, result.Error!, result.Output);

        // The containers have changed, and down forgets the deployed commit.
        var after = await StackAsync(user, route.EnvironmentId, route.StackId, route.StacksPage, cancellationToken);
        return after with { Notice = notice };
    }

    /// <summary>"Refresh" on a deployment: follow it again if it is still going.</summary>
    private async Task<StackScreen> FollowDeploymentAsync(
        BotUser user, DeploymentRoute route, Func<DashboardScreen, Task> showProgress, CancellationToken cancellationToken)
    {
        var screen = await StackAsync(user, route.EnvironmentId, route.StackId, route.StacksPage, cancellationToken);
        return await FollowAsync(user, screen, route.DeploymentId, showProgress, cancellationToken);
    }

    /// <summary>
    /// Keeps the message showing how the deployment goes until it ends: the status updates by itself, in the same
    /// message, with every button disabled meanwhile (a click would start a second interaction that this one would
    /// then overwrite). Stops after <see cref="FollowLimit"/>, within the 15 minutes Discord lets the message be edited.
    /// </summary>
    private async Task<StackScreen> FollowAsync(
        BotUser user, StackScreen stack, Guid deploymentId, Func<DashboardScreen, Task> showProgress, CancellationToken cancellationToken)
    {
        var id = deploymentId.ToString();
        var giveUpAt = time.GetUtcNow() + FollowLimit;
        var deployment = await forest.GetDeploymentAsync(user.Id, id, cancellationToken);
        Notice? shown = null;
        while (!deployment.IsFinished && time.GetUtcNow() < giveUpAt)
        {
            // Edit only when something changed: Discord limits how often one message may be edited.
            var notice = DescribeDeployment(deployment);
            if (notice != shown)
            {
                await showProgress(stack with { Notice = notice, DeploymentId = deploymentId, Busy = true });
                shown = notice;
            }

            await Task.Delay(FollowInterval, time, cancellationToken);
            deployment = await forest.GetDeploymentAsync(user.Id, id, cancellationToken);
        }

        // A finished deployment changes the deployed commit and the containers.
        var after = deployment.IsFinished
            ? await StackAsync(user, Guid.Parse(stack.Environment.Id), Guid.Parse(stack.Stack.Id), stack.StacksPage, cancellationToken)
            : stack;
        var final = DescribeDeployment(deployment);
        if (!deployment.IsFinished)
        {
            final = final with { Text = final.Text + " It is taking long: press Refresh to check again." };
        }

        return after with { Notice = final, DeploymentId = deploymentId, Busy = false };
    }

    public static Notice DescribeDeployment(ForestDeployment deployment) => deployment.Status switch
    {
        DeploymentStatus.Queued => new Notice(NoticeKind.Progress, "Deployment queued…"),
        DeploymentStatus.Running => new Notice(
            NoticeKind.Progress,
            deployment.Commit is { } commit ? $"Deploying commit {ShortCommit(commit)}…" : "Deploying…"),
        DeploymentStatus.Succeeded => new Notice(
            NoticeKind.Success, $"Deployed commit {ShortCommit(deployment.Commit ?? "")}."),
        DeploymentStatus.Failed => new Notice(NoticeKind.Failure, "Deployment failed: " + (deployment.Error ?? "no reason given.")),
        DeploymentStatus.Cancelled => new Notice(NoticeKind.Failure, "Deployment was cancelled."),
        _ => new Notice(NoticeKind.Info, "The deployment is in a state this bot does not know; check the panel."),
    };

    /// <summary>The first 7 characters, as git shows a commit.</summary>
    internal static string ShortCommit(string sha) => sha[..Math.Min(7, sha.Length)];

    private async Task<ForestEnvironment> FindEnvironmentAsync(BotUser user, Guid environmentId, CancellationToken cancellationToken)
    {
        var environments = await forest.ListEnvironmentsAsync(user.Id, cancellationToken);
        return environments.FirstOrDefault(e => Guid.TryParse(e.Id, out var id) && id == environmentId)
            ?? throw new ForestNotFoundException("This environment is gone, or you can no longer see it.");
    }

    private async Task<ForestStack> FindStackAsync(
        BotUser user, ForestEnvironment environment, Guid stackId, CancellationToken cancellationToken)
    {
        var stacks = await forest.ListStacksAsync(user.Id, environment.Id, cancellationToken);
        return stacks.FirstOrDefault(s => Guid.TryParse(s.Id, out var id) && id == stackId)
            ?? throw new ForestNotFoundException("This stack is gone, or you can no longer see it.");
    }

    /// <summary>
    /// The environment's containers, or null when the agent is offline or fails: the stacks are still worth showing,
    /// only without container counts.
    /// </summary>
    private async Task<IReadOnlyList<ForestContainer>?> TryListContainersAsync(
        BotUser user, ForestEnvironment environment, CancellationToken cancellationToken)
    {
        if (!environment.Online)
        {
            return null;
        }

        try
        {
            return await forest.ListContainersAsync(user.Id, environment.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is AgentUnavailableException or AgentFailedException)
        {
            logger.LogDebug("No containers for {Environment}: {Reason}", environment.Name, ex.Message);
            return null;
        }
    }
}
