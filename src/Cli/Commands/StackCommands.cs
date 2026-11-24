using System.CommandLine;
using ContainerForest.Cli.Api;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

internal sealed class StackListCommand() : EnvironmentCommand("list", "List the environment's stacks.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var stacks = (await context.Api.ListStacksAsync(environment.Id!.Value, context.CancellationToken))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        await context.Output.ListAsync(
            stacks,
            items =>
            {
                var table = new Table("NAME", "REPOSITORY", "BRANCH", "COMMIT", "DEPLOYED");
                foreach (var s in items)
                {
                    table.AddRow(s.Name, s.RepositoryName, s.Branch, CliOutput.ShortCommit(s.DeployedCommit), s.DeployedAt is null ? "not deployed" : CliOutput.Time(s.DeployedAt));
                }

                return table;
            },
            $"{environment.Name} has no stacks you can see.");
        return ExitCodes.Ok;
    }
}

internal sealed class StackShowCommand() : StackCommand("show", "Show a stack: its source, what is deployed, its settings.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        await context.Output.ItemAsync(stack, s =>
        [
            ("Name", s.Name),
            ("Id", s.Id?.ToString()),
            ("Environment", environment.Name),
            ("Repository", s.RepositoryName),
            ("Branch", s.Branch),
            ("Compose file", s.ComposePath),
            ("Deployed commit", s.DeployedCommit),
            ("Deployed at", s.DeployedAt is null ? "not deployed" : CliOutput.Time(s.DeployedAt)),
            ("Held", s.HeldCommit is null ? "no" : $"at {CliOutput.ShortCommit(s.HeldCommit)} since {CliOutput.Time(s.HeldSince)} (rollback)"),
            ("Deploy on push", CliOutput.YesNo(s.DeployOnPush)),
            ("Restart on file change", CliOutput.YesNo(s.RestartOnFileChange)),
            ("Deploy hook", CliOutput.YesNo(s.HasDeployHook)),
            ("Image hook", s.HasImageHook == true ? (s.ImageHookAllowsDowngrade == true ? "yes, may roll back" : "yes") : "no"),
            ("Require signed commits", CliOutput.YesNo(s.RequireSignedCommits)),
            ("Require image digests", CliOutput.YesNo(s.RequireImageDigests)),
            ("System stack", CliOutput.YesNo(s.IsSystem)),
            ("You may deploy", CliOutput.YesNo(s.CanDeploy)),
        ]);
        return ExitCodes.Ok;
    }
}

internal sealed class StackCreateCommand() : EnvironmentCommand("create", "Create a stack from a repository (Editor of the environment).")
{
    private readonly Argument<string> name = new("name") { Description = "Stack name: the compose project and the directory on the host." };
    private readonly Option<string> repository = new("--repo") { Description = "Repository name, address or id.", Required = true };
    private readonly Option<string> branch = new("--branch") { Description = "Branch to deploy.", DefaultValueFactory = _ => "main" };
    private readonly Option<string> compose = new("--compose") { Description = "Compose file in the repository.", DefaultValueFactory = _ => "compose.yaml" };

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(name);
        command.Add(repository);
        command.Add(branch);
        command.Add(compose);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var repo = await context.Api.FindRepositoryAsync(context.Value(repository)!, context.CancellationToken);
        var stack = await context.Api.CreateStackAsync(
            environment.Id!.Value, context.Value(name), repo.Id!.Value, context.Value(branch)!, context.Value(compose)!, context.CancellationToken);
        context.Output.Done($"Stack {stack.Name} created in {environment.Name}; deploy it with 'forest stack deploy {stack.Name}'.");
        return ExitCodes.Ok;
    }
}

internal sealed class StackSetSourceCommand() : StackCommand("set-source", "Change the stack's repository, branch or compose file (Editor).")
{
    private readonly Option<string?> repository = new("--repo") { Description = "Repository name, address or id." };
    private readonly Option<string?> branch = new("--branch") { Description = "Branch to deploy." };
    private readonly Option<string?> compose = new("--compose") { Description = "Compose file in the repository." };

    protected override void ConfigureStack(Command command)
    {
        command.Add(repository);
        command.Add(branch);
        command.Add(compose);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        var repositoryId = context.Value(repository) is { } repo
            ? (await context.Api.FindRepositoryAsync(repo, context.CancellationToken)).Id!.Value
            : stack.RepositoryId!.Value;
        var updated = await context.Api.ChangeStackSourceAsync(
            stack.Id!.Value, repositoryId, context.Value(branch) ?? stack.Branch!, context.Value(compose) ?? stack.ComposePath!, context.CancellationToken);
        context.Output.Done($"Stack {updated.Name} now deploys {updated.ComposePath} from {updated.RepositoryName}@{updated.Branch}; it takes effect on the next deploy.");
        return ExitCodes.Ok;
    }
}

internal sealed class StackRemoveCommand() : StackCommand("remove", "Remove a stack that is not deployed (Editor of the environment).")
{
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void ConfigureStack(Command command) => command.Add(yes);

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        Confirmation.Require(context, yes, $"remove stack {stack.Name} with its deployment history");
        await context.Api.RemoveStackAsync(stack.Id!.Value, context.CancellationToken);
        context.Output.Done($"Stack {stack.Name} removed; its files stay on the host.");
        return ExitCodes.Ok;
    }
}

/// <summary><c>forest stack deploy NAME [--pull]</c>: queues a deployment and, unless told not to, follows it to the end.</summary>
internal sealed class StackDeployCommand() : StackCommand("deploy", "Deploy the head of the stack's branch (redeploy; with --pull, redeploy with pull).")
{
    private readonly Option<bool> pull = new("--pull") { Description = "Pull every image again, for moving tags such as latest." };
    private readonly Option<bool> noWait = new("--no-wait") { Description = "Only queue the deployment and print its id." };
    private readonly Option<string?> approve = new("--approve")
    {
        Description = "Full SHA of an unsigned commit to deploy to a stack that requires signed commits; only while it is the branch head.",
    };

    protected override void ConfigureStack(Command command)
    {
        command.Add(pull);
        command.Add(noWait);
        command.Add(approve);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        var queued = await context.Api.DeployAsync(stack.Id!.Value, context.Value(pull), context.CancellationToken, context.Value(approve));
        if (stack.HeldCommit is not null)
        {
            context.Output.Note($"{stack.Name} is held at {CliOutput.ShortCommit(stack.HeldCommit)} after a rollback; deploying the branch releases the hold.");
        }

        return await StackDeployment.FollowAsync(context, stack, queued, context.Value(noWait));
    }
}

/// <summary>
/// <c>forest stack rollback NAME --to SHA</c>: deploys a commit the stack deployed successfully before and holds the
/// stack at it, so pushes, the deploy hook and image updates leave it alone until the branch is deployed again.
/// </summary>
internal sealed class StackRollbackCommand()
    : StackCommand("rollback", "Deploy a commit the stack ran before and hold it there (Editor); 'deploy' returns to the branch.")
{
    private readonly Option<string> to = new("--to")
    {
        Description = "The commit to roll back to: a full SHA or a prefix of at least 7 characters, from 'forest deployment list'.",
        Required = true,
    };

    private readonly Option<bool> approve = new("--approve")
    {
        Description = "For a stack that requires signed commits: roll back to the commit even if no signature endorses it.",
    };

    private readonly Option<bool> noWait = new("--no-wait") { Description = "Only queue the deployment and print its id." };

    protected override void ConfigureStack(Command command)
    {
        command.Add(to);
        command.Add(approve);
        command.Add(noWait);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        var commit = await ResolveAsync(context, stack, context.Value(to)!.Trim());
        var queued = await context.Api.RollbackAsync(stack.Id!.Value, commit, context.Value(approve), context.CancellationToken);
        return await StackDeployment.FollowAsync(context, stack, queued, context.Value(noWait));
    }

    /// <summary>The full SHA of the stack's successful deployments that starts with <paramref name="prefix"/>.</summary>
    internal static async Task<string> ResolveAsync(CommandContext context, StackResponse stack, string prefix)
    {
        if (prefix.Length < 7 || !prefix.All(char.IsAsciiHexDigit))
        {
            throw new CliException($"'{prefix}' is not a commit SHA or a prefix of at least 7 characters.");
        }

        var deployments = await context.Api.ListDeploymentsAsync(stack.Id!.Value, context.CancellationToken);
        var matches = deployments
            .Where(d => d.Status == DeploymentStatus.Succeeded && d.Commit is not null
                && d.Commit.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Commit!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return matches switch
        {
            [var single] => single,
            [] => throw new CliException(
                $"None of the recent successful deployments of {stack.Name} deployed a commit starting with {prefix}; see 'forest deployment list {stack.Name}'."),
            _ => throw new CliException($"Commit prefix {prefix} is ambiguous: {string.Join(", ", matches.Select(CliOutput.ShortCommit))}."),
        };
    }
}

/// <summary><c>forest stack release-hold NAME</c>: lets automatic deployments deploy a stack held after a rollback.</summary>
internal sealed class StackReleaseHoldCommand()
    : StackCommand("release-hold", "Let pushes, the deploy hook and image updates deploy a stack held after a rollback (Editor).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        if (stack.HeldCommit is null)
        {
            context.Output.Done($"{stack.Name} is not held.");
            return ExitCodes.Ok;
        }

        await context.Api.ReleaseHoldAsync(stack.Id!.Value, context.CancellationToken);
        context.Output.Done($"{stack.Name} is no longer held; the next push, deploy hook call or image update deploys its branch.");
        return ExitCodes.Ok;
    }
}

/// <summary>Prints a queued deployment's id, or follows it to the end, for deploy and rollback.</summary>
internal static class StackDeployment
{
    public static async Task<int> FollowAsync(CommandContext context, StackResponse stack, DeploymentDetailsResponse queued, bool noWait)
    {
        var id = queued.Id!.Value;
        if (noWait)
        {
            if (context.Output.IsJson)
            {
                await context.Output.ItemAsync(queued, _ => []);
            }
            else
            {
                context.Output.Out.WriteLine(id);
            }

            return ExitCodes.Ok;
        }

        context.Output.Note($"Deployment {id} of {stack.Name} queued; following it (Ctrl+C stops following, not the deployment).");
        var watcher = new DeploymentWatcher(context.Api, context.Output.IsJson ? new CliOutput(TextWriter.Null, context.Output.Error, OutputFormat.Table) : context.Output, context.Host.Time);
        var finished = await watcher.WatchAsync(id, follow: true, context.CancellationToken);
        return await DeploymentResult.ReportAsync(context, finished);
    }
}

/// <summary><c>forest stack restart|down NAME</c>: compose restart or down, with its output.</summary>
internal sealed class StackControlCommand(StackAction action, string description)
    : StackCommand(action.ToString().ToLowerInvariant(), description)
{
    private readonly Option<int?> timeout = new("--timeout", "-t") { Description = "Seconds each container gets to stop (0-300, default 10)." };
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void ConfigureStack(Command command)
    {
        command.Add(timeout);
        if (action == StackAction.Down)
        {
            command.Add(yes);
        }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        if (action == StackAction.Down)
        {
            Confirmation.Require(context, yes, $"stop and remove the containers of {stack.Name}");
        }

        context.Output.Note(action == StackAction.Down ? $"Taking {stack.Name} down…" : $"Restarting {stack.Name}…");
        var result = await context.Api.ControlStackAsync(stack.Id!.Value, action, context.Value(timeout), context.CancellationToken);
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

        context.Output.Done(action == StackAction.Down ? $"{stack.Name} is down." : $"{stack.Name} restarted.");
        return ExitCodes.Ok;
    }
}

/// <summary><c>forest stack deploy-on-push|restart-on-file-change NAME on|off</c>.</summary>
internal sealed class StackSwitchCommand(string name, string description, Func<ForestApi, Guid, bool, CancellationToken, Task<StackResponse>> set)
    : StackCommand(name, description)
{
    private readonly Argument<string> state = new("state") { Description = "on or off." };

    protected override void ConfigureStack(Command command)
    {
        state.AcceptOnlyFromAmong("on", "off");
        command.Add(state);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        var enabled = context.Value(state) == "on";
        await set(context.Api, stack.Id!.Value, enabled, context.CancellationToken);
        context.Output.Done($"{Name} is {(enabled ? "on" : "off")} for {stack.Name}.");
        return ExitCodes.Ok;
    }
}

internal sealed class StackHookCreateCommand() : StackCommand("create", "Create the stack's deploy hook for CI; a new one replaces the old (Editor).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        var hook = await context.Api.CreateDeployHookAsync(stack.Id!.Value, context.CancellationToken);
        var url = new Uri(context.Api.Server, $"api/deploy-hooks/{stack.Id}");
        if (context.Output.IsJson)
        {
            await context.Output.ItemAsync(hook, _ => []);
            return ExitCodes.Ok;
        }

        context.Output.Note($"Deploy hook of {stack.Name}, shown once. CI calls, after pushing an image:");
        context.Output.Note($"  curl -fsS -X POST -H \"Authorization: Bearer $FOREST_DEPLOY_HOOK\" {url}");
        context.Output.Note("Token:");
        context.Output.Secret(hook.Token ?? "");
        return ExitCodes.Ok;
    }
}

internal sealed class StackHookRemoveCommand() : StackCommand("remove", "Remove the stack's deploy hook (Editor).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        await context.Api.RemoveDeployHookAsync(stack.Id!.Value, context.CancellationToken);
        context.Output.Done($"The deploy hook of {stack.Name} is removed.");
        return ExitCodes.Ok;
    }
}

/// <summary>Adds or removes the stack's own exceptions to the compose policy (ADR-0012).</summary>
internal sealed class StackPolicyCommand(bool allow)
    : StackCommand(
        allow ? "allow" : "disallow",
        allow
            ? "Give the stack allowances beyond the environment's compose policy, e.g. docker-socket (administrators of the environment)."
            : "Take allowances away from the stack (administrators of the environment).")
{
    private readonly Argument<string[]> allowances = new("allowance") { Description = "Allowances such as privileged, port:443, path:/srv/media.", Arity = ArgumentArity.OneOrMore };

    protected override void ConfigureStack(Command command) => command.Add(allowances);

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        var given = context.Value(allowances) ?? [];
        var current = stack.PolicyAllowances ?? [];
        List<string> next = allow
            ? [.. current.Concat(given).Distinct(StringComparer.Ordinal)]
            : [.. current.Except(given, StringComparer.Ordinal)];
        var updated = await context.Api.SetPolicyAllowancesAsync(stack.Id!.Value, next, context.CancellationToken);
        var list = updated.PolicyAllowances is { Count: > 0 } a ? string.Join(", ", a) : "none";
        context.Output.Done($"Allowances of {stack.Name}: {list}.");
        return ExitCodes.Ok;
    }
}
