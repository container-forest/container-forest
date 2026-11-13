using ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Commands;

public sealed record StackList(ForestEnvironment Environment, IReadOnlyList<ForestStack> Stacks);

/// <summary>/stacks: the stacks of one environment with what the user may do with them.</summary>
public sealed class StacksCommand(IForestClient forest, EnvironmentResolver environments, ILogger<StacksCommand> logger)
    : BotCommand<string, StackList>(logger)
{
    public override string Name => "stacks";

    protected override async Task<StackList> CallForestAsync(BotUser user, string environment, CancellationToken cancellationToken)
    {
        var resolved = await environments.ResolveAsync(user, environment, cancellationToken);
        var stacks = await forest.ListStacksAsync(user.Id, resolved.Id, cancellationToken);
        return new StackList(resolved, stacks);
    }

    protected override BotReply Format(StackList result, string environment) =>
        BotReply.Success(ReplyFormatter.Stacks(result.Environment, result.Stacks));
}

/// <param name="Stack">The stack's name in the environment.</param>
/// <param name="TimeoutSeconds">Seconds Docker waits for each container before killing it; null for the default.</param>
public sealed record StackActionArgs(string Environment, string Stack, int? TimeoutSeconds = null);

public sealed record StackActionOutcome(ForestEnvironment Environment, ForestStackResult Result);

/// <summary>
/// /stack-restart, /stack-down, and for unmanaged stacks /stack-up and /stack-pull. The aggregator decides whether the user may do it and whether the stack allows it;
/// the bot reports its answer together with what compose printed, which matters most when compose failed.
/// </summary>
public abstract class StackActionCommand(IForestClient forest, EnvironmentResolver environments, ILogger logger)
    : BotCommand<StackActionArgs, StackActionOutcome>(logger)
{
    public abstract StackAction Action { get; }

    /// <summary>The past tense for the reply, e.g. "Restarted".</summary>
    protected abstract string Done { get; }

    protected override async Task<StackActionOutcome> CallForestAsync(
        BotUser user, StackActionArgs args, CancellationToken cancellationToken)
    {
        var environment = await environments.ResolveAsync(user, args.Environment, cancellationToken);
        var result = await forest.ControlStackAsync(
            user.Id, environment.Id, args.Stack.Trim(), Action, args.TimeoutSeconds, cancellationToken);
        return new StackActionOutcome(environment, result);
    }

    protected override BotReply Format(StackActionOutcome outcome, StackActionArgs args)
    {
        var stack = $"{Markdown.Code(args.Stack.Trim())} in **{Markdown.Escape(outcome.Environment.Name)}**";
        var result = outcome.Result;
        return result.Succeeded
            ? BotReply.Success(ReplyFormatter.WithOutput($"✅ {Done} stack {stack}.", result.Output))
            : new BotReply(
                ReplyFormatter.WithOutput($"❌ Stack {stack}: {Markdown.Escape(result.Error!)}", result.Output),
                IsError: true);
    }
}

public sealed class RestartStackCommand(IForestClient forest, EnvironmentResolver environments, ILogger<RestartStackCommand> logger)
    : StackActionCommand(forest, environments, logger)
{
    public override string Name => "stack-restart";

    public override StackAction Action => StackAction.Restart;

    protected override string Done => "Restarted";
}

public sealed class DownStackCommand(IForestClient forest, EnvironmentResolver environments, ILogger<DownStackCommand> logger)
    : StackActionCommand(forest, environments, logger)
{
    public override string Name => "stack-down";

    public override StackAction Action => StackAction.Down;

    protected override string Done => "Took down";
}

/// <summary>/stack-up: docker compose up -d of an unmanaged stack (ADR-0016); managed stacks are deployed instead.</summary>
public sealed class UpStackCommand(IForestClient forest, EnvironmentResolver environments, ILogger<UpStackCommand> logger)
    : StackActionCommand(forest, environments, logger)
{
    public override string Name => "stack-up";

    public override StackAction Action => StackAction.Up;

    protected override string Done => "Brought up";
}

/// <summary>/stack-pull: docker compose pull of an unmanaged stack (ADR-0016); run /stack-up afterwards to use the images.</summary>
public sealed class PullStackCommand(IForestClient forest, EnvironmentResolver environments, ILogger<PullStackCommand> logger)
    : StackActionCommand(forest, environments, logger)
{
    public override string Name => "stack-pull";

    public override StackAction Action => StackAction.Pull;

    protected override string Done => "Pulled images of";
}
