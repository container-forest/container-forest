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

/// <summary><c>forest container start|stop|restart NAME</c>.</summary>
internal sealed class ContainerControlCommand(ContainerAction action, string description)
    : EnvironmentCommand(action.ToString().ToLowerInvariant(), description)
{
    private readonly Argument<string> container = new("container") { Description = "Container name, id or unique id prefix." };

    private readonly Option<int?> timeout = new("--timeout", "-t")
    {
        Description = "Seconds to wait for the container to stop before killing it (0-300; 0 kills at once).",
    };

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(container);
        if (action != ContainerAction.Start)
        {
            command.Add(timeout);
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var name = context.Value(container);
        await context.Api.ControlContainerAsync(environment.Id!.Value, name, action, context.Value(timeout), context.CancellationToken);
        context.Output.Done(action switch
        {
            ContainerAction.Start => $"Started {name}.",
            ContainerAction.Stop => $"Stopped {name}.",
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
