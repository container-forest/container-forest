using System.CommandLine;
using ContainerForest.Cli.Api;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

internal sealed class ContainerListCommand() : EnvironmentCommand("list", "List the environment's containers as its agent sees them now.")
{
    private readonly Option<string?> stack = new("--stack") { Description = "Only containers of this stack." };
    private readonly Option<bool> running = new("--running") { Description = "Only running containers." };

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(stack);
        command.Add(running);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var containers = (await context.Api.ListContainersAsync(environment.Id!.Value, context.CancellationToken))
            .Where(c => context.Value(stack) is not { } name || c.StackName == name)
            .Where(c => !context.Value(running) || c.State == ContainerState.Running)
            .OrderBy(c => c.StackName ?? "￿", StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        await context.Output.ListAsync(
            containers,
            items =>
            {
                var table = new Table("NAME", "STATE", "STACK", "IMAGE", "STATUS");
                foreach (var c in items)
                {
                    table.AddRow(c.Name, c.State?.ToString().ToLowerInvariant(), c.StackName, c.Image, c.StatusText);
                }

                return table;
            },
            "No containers match.");
        return ExitCodes.Ok;
    }
}

/// <summary><c>forest container start|stop|restart|remove NAME</c>; remove needs <c>--yes</c>.</summary>
internal sealed class ContainerControlCommand(ContainerAction action, string description)
    : EnvironmentCommand(action.ToString().ToLowerInvariant(), description)
{
    private readonly Argument<string> container = new("container") { Description = "Container name, id or unique id prefix." };

    private readonly Option<int?> timeout = new("--timeout", "-t")
    {
        Description = "Seconds to wait for the container to stop before killing it (0-300; 0 kills at once).",
    };

    private readonly Option<bool> yes = Confirmation.Option();

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(container);
        if (action is ContainerAction.Stop or ContainerAction.Restart)
        {
            command.Add(timeout);
        }

        if (action == ContainerAction.Remove)
        {
            command.Add(yes);
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var name = context.Value(container);
        if (action == ContainerAction.Remove)
        {
            Confirmation.Require(context, yes, $"remove the container {name}");
        }

        await context.Api.ControlContainerAsync(environment.Id!.Value, name, action, context.Value(timeout), context.CancellationToken);
        context.Output.Done(action switch
        {
            ContainerAction.Start => $"Started {name}.",
            ContainerAction.Stop => $"Stopped {name}.",
            ContainerAction.Remove => $"Removed {name}.",
            _ => $"Restarted {name}.",
        });
        return ExitCodes.Ok;
    }
}

/// <summary><c>forest container logs NAME [-f]</c>: the end of a container's log; with <c>-f</c> new lines as they come.</summary>
internal sealed class ContainerLogsCommand() : EnvironmentCommand("logs", "Show a container's log.")
{
    private static readonly TimeSpan FollowInterval = TimeSpan.FromSeconds(2);

    private readonly Argument<string> container = new("container") { Description = "Container name, id or unique id prefix." };
    private readonly Option<int> tail = new("--tail", "-n") { Description = "Lines from the end (1-5000).", DefaultValueFactory = _ => 200 };
    private readonly Option<DateTimeOffset?> since = new("--since") { Description = "Only lines from this time on (ISO 8601)." };
    private readonly Option<bool> follow = new("--follow", "-f") { Description = "Keep showing new lines until Ctrl+C." };
    private readonly Option<bool> timestamps = new("--timestamps") { Description = "Show each line's time." };

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(container);
        command.Add(tail);
        command.Add(since);
        command.Add(follow);
        command.Add(timestamps);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var environmentId = environment.Id!.Value;
        var name = context.Value(container);
        var follower = new LogFollower();
        var lines = await context.Api.GetContainerLogsAsync(environmentId, name, context.Value(tail), context.Value(since), context.CancellationToken);
        await WriteAsync(context, follower.Accept(lines));

        while (context.Value(follow))
        {
            await Task.Delay(FollowInterval, context.Host.Time, context.CancellationToken);
            lines = await context.Api.GetContainerLogsAsync(environmentId, name, 5000, follower.Since, context.CancellationToken);
            await WriteAsync(context, follower.Accept(lines));
        }

        return ExitCodes.Ok;
    }

    private async Task WriteAsync(CommandContext context, IReadOnlyList<ContainerLogLineResponse> lines)
    {
        foreach (var line in lines)
        {
            // Container output goes where the container wrote it: stderr lines to standard error.
            var writer = line.Stream == LogStream.Stderr ? context.Output.Error : context.Output.Out;
            var text = context.Value(timestamps) ? $"{line.At?.ToString("O") ?? "-"} {line.Text}" : line.Text;
            await writer.WriteLineAsync(text);
        }

        await context.Output.Out.FlushAsync(context.CancellationToken);
    }
}

/// <summary><c>forest unmanaged list</c>: compose projects run by hand on the host (ADR-0016).</summary>
internal sealed class UnmanagedListCommand() : EnvironmentCommand("list", "List the compose projects run by hand on the environment's host.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var stacks = await context.Api.ListUnmanagedStacksAsync(environment.Id!.Value, context.CancellationToken);
        await context.Output.ListAsync(
            stacks,
            items =>
            {
                var table = new Table("NAME", "STATE", "DIRECTORY", "ACTIONS");
                foreach (var s in items)
                {
                    var actions = s.IsSystem == true ? "system"
                        : s.CanDeploy == true ? "up, pull, restart, down"
                        : s.CanRestart == true ? "restart"
                        : "view only";
                    table.AddRow(s.Name, s.HasContainers == true ? "containers" : "no containers", s.Directory, actions);
                }

                return table;
            },
            "No unmanaged stacks: the agent has no unmanaged stack roots, or none has a compose project.");
        return ExitCodes.Ok;
    }
}

/// <summary><c>forest unmanaged up|pull|restart|down NAME</c>; down needs <c>--yes</c>.</summary>
internal sealed class UnmanagedControlCommand(UnmanagedStackAction action, string description)
    : EnvironmentCommand(action.ToString().ToLowerInvariant(), description)
{
    private readonly Argument<string> stack = new("stack") { Description = "The compose project name." };
    private readonly Option<int?> timeout = new("--timeout", "-t") { Description = "Seconds each container gets to stop (0-300, default 10)." };
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(stack);
        if (action is UnmanagedStackAction.Restart or UnmanagedStackAction.Down)
        {
            command.Add(timeout);
        }

        if (action == UnmanagedStackAction.Down)
        {
            command.Add(yes);
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var name = context.Value(stack);
        if (action == UnmanagedStackAction.Down)
        {
            Confirmation.Require(context, yes, $"stop and remove the containers of {name}");
        }

        context.Output.Note(action switch
        {
            UnmanagedStackAction.Up => $"Bringing {name} up…",
            UnmanagedStackAction.Pull => $"Pulling the images of {name}…",
            UnmanagedStackAction.Restart => $"Restarting {name}…",
            _ => $"Taking {name} down…",
        });
        var result = await context.Api.ControlUnmanagedStackAsync(
            environment.Id!.Value, name, action, context.Value(timeout), context.CancellationToken);
        if (context.Output.IsJson)
        {
            await context.Output.ItemAsync(result, _ => []);
        }
        else
        {
            foreach (var line in result.Output ?? [])
            {
                await context.Output.Out.WriteLineAsync(line);
            }
        }

        if (result.Succeeded != true)
        {
            throw new CliException(result.Error ?? "Compose failed.");
        }

        context.Output.Done(action switch
        {
            UnmanagedStackAction.Up => $"{name} is up.",
            UnmanagedStackAction.Pull => $"Pulled the images of {name}; 'forest unmanaged up {name}' uses them.",
            UnmanagedStackAction.Restart => $"{name} restarted.",
            _ => $"{name} is down.",
        });
        return ExitCodes.Ok;
    }
}
