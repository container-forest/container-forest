using System.Text.RegularExpressions;
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

/// <summary>/remove: docker rm of a stopped container; the aggregator refuses a running one.</summary>
public sealed class RemoveContainerCommand(
    IForestClient forest, EnvironmentResolver environments, ILogger<RemoveContainerCommand> logger)
    : ContainerActionCommand(forest, environments, logger)
{
    public override ContainerAction Action => ContainerAction.Remove;

    protected override string Done => "Removed";

    protected override int? StopTimeout(ContainerActionArgs args) => null;
}

/// <summary>The end of a container's log, for the "Logs" button under a notification: as much as fits in one message.</summary>
/// <remarks>
/// Logs can hold secrets the application printed itself; the aggregator lets anyone with ViewLogs read them, as in the
/// panel, and the reply, like every reply of the bot, is seen only by the user who asked.
/// </remarks>
public sealed partial class ContainerLogsCommand(
    IForestClient forest, EnvironmentResolver environments, ILogger<ContainerLogsCommand> logger)
    : BotCommand<ContainerActionArgs, ContainerLogs>(logger)
{
    /// <summary>More than fit in a message as a rule; the reply keeps the last ones that do.</summary>
    public const int TailLines = 40;

    /// <summary>A longer line (Docker allows 16 KB) would leave room for no other: it keeps its start.</summary>
    public const int MaxLineLength = 300;

    public override string Name => "logs";

    protected override async Task<ContainerLogs> CallForestAsync(
        BotUser user, ContainerActionArgs args, CancellationToken cancellationToken)
    {
        var environment = await environments.ResolveAsync(user, args.Environment, cancellationToken);
        var lines = await forest.GetContainerLogsAsync(user.Id, environment.Id, args.Container.Trim(), TailLines, cancellationToken);
        return new ContainerLogs(environment, lines);
    }

    protected override BotReply Format(ContainerLogs result, ContainerActionArgs args)
    {
        var header = $"📜 Log of {Markdown.Code(args.Container.Trim())} in **{Markdown.Escape(result.Environment.Name)}**";
        var lines = result.Lines.Select(l => Readable(l.Text)).Where(l => l.Trim().Length > 0).ToList();
        return BotReply.Success(lines.Count == 0
            ? header + ": no lines."
            : ReplyFormatter.WithOutput(header + ":", lines));
    }

    /// <summary>The line as a code block shows it: without terminal colours and control characters, cut to <see cref="MaxLineLength"/>.</summary>
    public static string Readable(string text)
    {
        var plain = ControlCharacters().Replace(AnsiEscape().Replace(text, ""), "");
        return plain.Length <= MaxLineLength ? plain : plain[..(MaxLineLength - 1)] + "…";
    }

    // CSI sequences (colours, cursor moves) and two-character escapes such as ESC 7; a lone ESC goes with the rest below.
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|[0-Z\\-~])")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"[\x00-\x08\x0A-\x1F\x7F]")]
    private static partial Regex ControlCharacters();
}

/// <summary>The lines of a log and the environment they were read in, for the reply.</summary>
public sealed record ContainerLogs(ForestEnvironment Environment, IReadOnlyList<ForestLogLine> Lines);

/// <summary>/link: links the Discord account with a one-time code from the panel.</summary>
public sealed class LinkCommand(IForestClient forest, ILogger<LinkCommand> logger) : BotCommand<string, string>(logger)
{
    public override string Name => "link";

    protected override Task<string> CallForestAsync(BotUser user, string code, CancellationToken cancellationToken) =>
        forest.LinkAccountAsync(user.Id, user.UserName, code.Trim(), cancellationToken);

    protected override BotReply Format(string userName, string code) =>
        BotReply.Success($"🔗 Your Discord account is now linked to **{Markdown.Escape(userName)}**.");
}

/// <param name="Stack">Only this compose project's containers; null for all.</param>
public sealed record StatsArgs(string Environment, string? Stack = null);

public sealed record StatsResult(ForestEnvironment Environment, string? Stack, ForestStats Stats);

/// <summary>/stats: the host and the busiest containers from the agent's latest resource usage sample (ADR-0017).</summary>
public sealed class StatsCommand(IForestClient forest, EnvironmentResolver environments, ILogger<StatsCommand> logger)
    : BotCommand<StatsArgs, StatsResult>(logger)
{
    public override string Name => "stats";

    protected override async Task<StatsResult> CallForestAsync(BotUser user, StatsArgs args, CancellationToken cancellationToken)
    {
        var environment = await environments.ResolveAsync(user, args.Environment, cancellationToken);
        var stack = string.IsNullOrWhiteSpace(args.Stack) ? null : args.Stack.Trim();
        return new StatsResult(environment, stack, await forest.GetStatsAsync(user.Id, environment.Id, stack, cancellationToken));
    }

    protected override BotReply Format(StatsResult result, StatsArgs args) =>
        BotReply.Success(ReplyFormatter.Stats(result.Environment, result.Stack, result.Stats));
}
