using ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Commands;

/// <summary>/environments: the environments the user can see, with their agents' status.</summary>
public sealed class EnvironmentsCommand(IForestClient forest, ILogger<EnvironmentsCommand> logger)
    : BotCommand<NoArgs, IReadOnlyList<ForestEnvironment>>(logger)
{
    public override string Name => "environments";

    protected override Task<IReadOnlyList<ForestEnvironment>> CallForestAsync(
        BotUser user, NoArgs args, CancellationToken cancellationToken) =>
        forest.ListEnvironmentsAsync(user.Id, cancellationToken);

    protected override BotReply Format(IReadOnlyList<ForestEnvironment> result, NoArgs args) =>
        BotReply.Success(ReplyFormatter.Environments(result));
}

public sealed record ContainerList(ForestEnvironment Environment, IReadOnlyList<ForestContainer> Containers);

/// <summary>/containers: the containers of one environment, grouped by stack.</summary>
public sealed class ContainersCommand(IForestClient forest, EnvironmentResolver environments, ILogger<ContainersCommand> logger)
    : BotCommand<string, ContainerList>(logger)
{
    public override string Name => "containers";

    protected override async Task<ContainerList> CallForestAsync(BotUser user, string environment, CancellationToken cancellationToken)
    {
        var resolved = await environments.ResolveAsync(user, environment, cancellationToken);
        var containers = await forest.ListContainersAsync(user.Id, resolved.Id, cancellationToken);
        return new ContainerList(resolved, containers);
    }

    protected override BotReply Format(ContainerList result, string environment) =>
        BotReply.Success(ReplyFormatter.Containers(result.Environment, result.Containers));
}

/// <param name="Container">A full id, an exact name or a unique id prefix, as in the docker CLI.</param>
/// <param name="TimeoutSeconds">For stop and restart: seconds before Docker kills the process; null for the default.</param>
public sealed record ContainerActionArgs(string Environment, string Container, int? TimeoutSeconds = null);

/// <summary>
/// /start, /stop, /restart. Whether the user may do it and whether it makes sense in the container's state is
/// decided by the aggregator; the bot only reports its answer.
/// </summary>
public abstract class ContainerActionCommand(IForestClient forest, EnvironmentResolver environments, ILogger logger)
    : BotCommand<ContainerActionArgs, ForestEnvironment>(logger)
{
    public abstract ContainerAction Action { get; }

    /// <summary>The past tense for the reply, e.g. "Restarted".</summary>
    protected abstract string Done { get; }

    public override string Name => Action.ToString().ToLowerInvariant();

    protected override async Task<ForestEnvironment> CallForestAsync(
        BotUser user, ContainerActionArgs args, CancellationToken cancellationToken)
    {
        var environment = await environments.ResolveAsync(user, args.Environment, cancellationToken);
        await forest.ControlContainerAsync(
            user.Id, environment.Id, args.Container.Trim(), Action, StopTimeout(args), cancellationToken);
        return environment;
    }

    /// <summary>The stop timeout to send; only actions that stop the process have one.</summary>
    protected virtual int? StopTimeout(ContainerActionArgs args) => args.TimeoutSeconds;

    protected override BotReply Format(ForestEnvironment result, ContainerActionArgs args) =>
        BotReply.Success($"✅ {Done} {Markdown.Code(args.Container.Trim())} in **{Markdown.Escape(result.Name)}**.");
}

public sealed class StartContainerCommand(IForestClient forest, EnvironmentResolver environments, ILogger<StartContainerCommand> logger)
    : ContainerActionCommand(forest, environments, logger)
{
    public override ContainerAction Action => ContainerAction.Start;

    protected override string Done => "Started";

    protected override int? StopTimeout(ContainerActionArgs args) => null;
}

public sealed class StopContainerCommand(IForestClient forest, EnvironmentResolver environments, ILogger<StopContainerCommand> logger)
    : ContainerActionCommand(forest, environments, logger)
{
    public override ContainerAction Action => ContainerAction.Stop;

    protected override string Done => "Stopped";
}

public sealed class RestartContainerCommand(
    IForestClient forest, EnvironmentResolver environments, ILogger<RestartContainerCommand> logger)
    : ContainerActionCommand(forest, environments, logger)
{
    public override ContainerAction Action => ContainerAction.Restart;

    protected override string Done => "Restarted";
}

/// <summary>/link: links the Discord account with a one-time code from the panel.</summary>
public sealed class LinkCommand(IForestClient forest, ILogger<LinkCommand> logger) : BotCommand<string, string>(logger)
{
    public override string Name => "link";

    protected override Task<string> CallForestAsync(BotUser user, string code, CancellationToken cancellationToken) =>
        forest.LinkAccountAsync(user.Id, user.UserName, code.Trim(), cancellationToken);

    protected override BotReply Format(string userName, string code) =>
        BotReply.Success($"🔗 Your Discord account is now linked to **{Markdown.Escape(userName)}**.");
}
