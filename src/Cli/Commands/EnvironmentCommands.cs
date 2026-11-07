using System.CommandLine;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

/// <summary>A command about an environment itself, named as its argument: <c>forest env show r2</c>.</summary>
internal abstract class EnvironmentArgumentCommand(string name, string description) : ForestCommand(name, description)
{
    private readonly Argument<string> environmentArgument = new("environment") { Description = "Environment name or id." };

    protected override void Configure(Command command)
    {
        command.Add(environmentArgument);
        ConfigureEnvironment(command);
    }

    protected sealed override async Task<int> ExecuteAsync(CommandContext context)
    {
        var environment = await context.Api.ResolveEnvironmentAsync(context.Value(environmentArgument), null, context.CancellationToken);
        return await ExecuteAsync(context, environment);
    }

    protected virtual void ConfigureEnvironment(Command command)
    {
    }

    protected abstract Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment);

    internal static string AgentStatus(EnvironmentResponse environment) =>
        environment.IsEnrolled != true ? "not enrolled" : environment.IsOnline == true ? "online" : "offline";
}

internal sealed class EnvListCommand() : ForestCommand("list", "List environments you can see and their agents.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var environments = await context.Api.ListEnvironmentsAsync(context.CancellationToken);
        await context.Output.ListAsync(
            environments,
            items =>
            {
                var table = new Table("NAME", "AGENT", "HOST", "VERSION", "ROLE", "PRIVATE");
                foreach (var e in items)
                {
                    table.AddRow(e.Name, EnvironmentArgumentCommand.AgentStatus(e), e.AgentHostname, e.AgentVersion, e.Role?.ToString(), CliOutput.YesNo(e.IsPrivate));
                }

                return table;
            },
            "You can see no environments.");
        return ExitCodes.Ok;
    }
}

internal sealed class EnvShowCommand() : EnvironmentArgumentCommand("show", "Show an environment and its agent.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        await context.Output.ItemAsync(environment, e =>
        [
            ("Name", e.Name),
            ("Id", e.Id?.ToString()),
            ("Private", CliOutput.YesNo(e.IsPrivate)),
            ("Agent", AgentStatus(e)),
            ("Host", e.AgentHostname),
            ("Agent version", e.AgentVersion),
            ("Last seen", CliOutput.Time(e.LastSeenAt)),
            ("Your role", e.Role?.ToString()),
            ("Enrollment token until", e.EnrollmentTokenExpiresAt is null ? null : CliOutput.Time(e.EnrollmentTokenExpiresAt)),
        ]);
        return ExitCodes.Ok;
    }
}

internal sealed class EnvCreateCommand() : ForestCommand("create", "Create an environment and get the agent's enrollment token (administrators).")
{
    private readonly Argument<string> name = new("name") { Description = "Environment name." };
    private readonly Option<bool> isPrivate = new("--private") { Description = "Only users with a role on it see it." };

    protected override void Configure(Command command)
    {
        command.Add(name);
        command.Add(isPrivate);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var created = await context.Api.CreateEnvironmentAsync(context.Value(name), context.Value(isPrivate), context.CancellationToken);
        if (context.Output.IsJson)
        {
            await context.Output.ItemAsync(created, _ => []);
            return ExitCodes.Ok;
        }

        context.Output.Note($"Environment {created.Name} created. The agent's enrollment token, shown once, valid until {CliOutput.Time(created.ExpiresAt)}:");
        context.Output.Secret(created.EnrollmentToken ?? "");
        context.Output.Note("Set it as ENROLLMENT_TOKEN in the agent's .env; the panel's environment screen has the compose file.");
        return ExitCodes.Ok;
    }
}

internal sealed class EnvUpdateCommand() : EnvironmentArgumentCommand("update", "Rename an environment or change who sees it (administrators).")
{
    private readonly Option<string?> name = new("--name") { Description = "New name." };
    private readonly Option<bool> makePrivate = new("--private") { Description = "Only users with a role on it see it." };
    private readonly Option<bool> makePublic = new("--public") { Description = "Every user sees it as a Viewer." };

    protected override void ConfigureEnvironment(Command command)
    {
        command.Add(name);
        command.Add(makePrivate);
        command.Add(makePublic);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        if (context.Value(makePrivate) && context.Value(makePublic))
        {
            throw new CliException("Choose --private or --public, not both.", ExitCodes.Usage);
        }

        var isPrivate = context.Value(makePrivate) || (!context.Value(makePublic) && environment.IsPrivate == true);
        var updated = await context.Api.UpdateEnvironmentAsync(
            environment.Id!.Value, context.Value(name) ?? environment.Name!, isPrivate, context.CancellationToken);
        context.Output.Done($"Environment {updated.Name} updated{(updated.IsPrivate == true ? " (private)" : "")}.");
        return ExitCodes.Ok;
    }
}

internal sealed class EnvRemoveCommand() : EnvironmentArgumentCommand("remove", "Remove an environment without stacks (administrators).")
{
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void ConfigureEnvironment(Command command) => command.Add(yes);

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        Confirmation.Require(context, yes, $"remove environment {environment.Name} with its age keys, registry logins and roles");
        await context.Api.RemoveEnvironmentAsync(environment.Id!.Value, context.CancellationToken);
        context.Output.Done($"Environment {environment.Name} removed.");
        return ExitCodes.Ok;
    }
}

internal sealed class EnvEnrollmentTokenCommand()
    : EnvironmentArgumentCommand("enrollment-token", "Issue a new enrollment token for the agent (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var token = await context.Api.IssueEnrollmentTokenAsync(environment.Id!.Value, context.CancellationToken);
        if (context.Output.IsJson)
        {
            await context.Output.ItemAsync(token, _ => []);
            return ExitCodes.Ok;
        }

        context.Output.Note($"New enrollment token for {environment.Name}, shown once, valid until {CliOutput.Time(token.ExpiresAt)}; the previous one stops working:");
        context.Output.Secret(token.EnrollmentToken ?? "");
        return ExitCodes.Ok;
    }
}

internal sealed class EnvRevokeAgentCommand()
    : EnvironmentArgumentCommand("revoke-agent", "Forget the agent's credentials and disconnect it (administrators).")
{
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void ConfigureEnvironment(Command command) => command.Add(yes);

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        Confirmation.Require(context, yes, $"disconnect the agent of {environment.Name}; it needs a new enrollment token to come back");
        await context.Api.RevokeAgentAsync(environment.Id!.Value, context.CancellationToken);
        context.Output.Done($"The agent of {environment.Name} is revoked.");
        return ExitCodes.Ok;
    }
}

internal sealed class KeyListCommand() : EnvironmentCommand("list", "List the environment's age keys (public parts).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var keys = await context.Api.ListAgeKeysAsync(environment.Id!.Value, context.CancellationToken);
        await context.Output.ListAsync(
            keys,
            items =>
            {
                var table = new Table("ID", "RECIPIENT", "STACK", "CREATED");
                foreach (var key in items)
                {
                    table.AddRow(key.Id?.ToString(), key.Recipient, key.StackId?.ToString() ?? "(environment)", CliOutput.Time(key.CreatedAt));
                }

                return table;
            },
            $"{environment.Name} has no age keys.");
        return ExitCodes.Ok;
    }
}

internal sealed class KeyCreateCommand() : EnvironmentCommand("create", "Create a new age key for the environment or one of its stacks (its administrators).")
{
    private readonly Option<string?> stack = new("--stack")
    {
        Description = "A stack of the environment: only its deployments and Editors decrypt with the key (ADR-0012).",
    };

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(stack);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var stackId = context.Value(stack) is { } name ? (await context.Api.FindStackAsync(environment, name, context.CancellationToken)).Id : null;
        var key = await context.Api.CreateAgeKeyAsync(environment.Id!.Value, stackId, context.CancellationToken);
        if (context.Output.IsJson)
        {
            await context.Output.ItemAsync(key, _ => []);
            return ExitCodes.Ok;
        }

        context.Output.Note($"New age key for {environment.Name}; add the recipient to .sops.yaml and run 'sops updatekeys':");
        context.Output.Secret(key.Recipient ?? "");
        return ExitCodes.Ok;
    }
}

internal sealed class KeyRemoveCommand() : EnvironmentCommand("remove", "Remove an age key (administrators).")
{
    private readonly Argument<Guid> key = new("key") { Description = "Key id, see 'forest env key list'." };
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(key);
        command.Add(yes);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        Confirmation.Require(context, yes, "remove the key; files encrypted only for it can no longer be decrypted");
        await context.Api.RemoveAgeKeyAsync(environment.Id!.Value, context.Value(key), context.CancellationToken);
        context.Output.Done("Age key removed.");
        return ExitCodes.Ok;
    }
}

internal sealed class RegistryListCommand() : EnvironmentCommand("list", "List registry logins of the environment (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var credentials = await context.Api.ListRegistryCredentialsAsync(environment.Id!.Value, context.CancellationToken);
        await context.Output.ListAsync(
            credentials,
            items =>
            {
                var table = new Table("SERVER", "USERNAME", "UPDATED", "ID");
                foreach (var credential in items)
                {
                    table.AddRow(credential.Server, credential.Username, CliOutput.Time(credential.UpdatedAt), credential.Id?.ToString());
                }

                return table;
            },
            $"{environment.Name} has no registry logins.");
        return ExitCodes.Ok;
    }
}

internal sealed class RegistrySetCommand() : EnvironmentCommand("set", "Set the login for a registry; the password is typed or read from standard input (administrators).")
{
    private readonly Argument<string> server = new("server") { Description = "Registry host, e.g. ghcr.io." };
    private readonly Option<string> username = new("--username", "-u") { Description = "User name.", Required = true };

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(server);
        command.Add(username);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var password = await SecretInput.ReadAsync(context, "Password or token");

        var credential = await context.Api.SetRegistryCredentialAsync(
            environment.Id!.Value, context.Value(server), context.Value(username)!, password, context.CancellationToken);
        context.Output.Done($"Login for {credential.Server} saved in {environment.Name}.");
        return ExitCodes.Ok;
    }
}

internal sealed class RegistryRemoveCommand() : EnvironmentCommand("remove", "Remove a registry login (administrators).")
{
    private readonly Argument<string> server = new("server") { Description = "Registry host or login id." };

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(server);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var requested = context.Value(server);
        var credentials = await context.Api.ListRegistryCredentialsAsync(environment.Id!.Value, context.CancellationToken);
        var credential = credentials.FirstOrDefault(c => c.Id?.ToString() == requested || string.Equals(c.Server, requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliException($"{environment.Name} has no login for {requested}.", ExitCodes.Denied);
        await context.Api.RemoveRegistryCredentialAsync(environment.Id!.Value, credential.Id!.Value, context.CancellationToken);
        context.Output.Done($"Login for {credential.Server} removed from {environment.Name}.");
        return ExitCodes.Ok;
    }
}

internal sealed class PolicyShowCommand() : EnvironmentCommand("show", "Show the environment's compose policy (ADR-0012).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var policy = await context.Api.GetComposePolicyAsync(environment.Id!.Value, context.CancellationToken);
        await context.Output.ItemAsync(policy, p =>
        [
            ("Mode", p.Mode?.ToString()),
            ("Data root", p.DataRoot),
            ("System read-only paths", string.Join(", ", p.SystemReadOnlyPaths ?? [])),
            ("Allowances", p.Allowances is { Count: > 0 } a ? string.Join(", ", a) : "none"),
            ("Protected paths", string.Join(", ", p.ProtectedPaths ?? [])),
        ]);
        return ExitCodes.Ok;
    }
}

/// <summary>Changes parts of the policy; what is not given stays as it is.</summary>
internal sealed class PolicySetCommand() : EnvironmentCommand("set", "Change the environment's compose policy (its administrators).")
{
    private readonly Option<ComposePolicyMode?> mode = new("--mode") { Description = "Off, Audit (warnings only) or Enforce." };
    private readonly Option<string?> dataRoot = new("--data-root") { Description = "Parent of the stacks' data directories." };
    private readonly Option<string[]> allow = new("--allow") { Description = "Add an allowance for every stack, e.g. network:proxy.", AllowMultipleArgumentsPerToken = true };
    private readonly Option<string[]> disallow = new("--disallow") { Description = "Remove an allowance.", AllowMultipleArgumentsPerToken = true };
    private readonly Option<string[]> systemPath = new("--system-path") { Description = "Add a read-only system path.", AllowMultipleArgumentsPerToken = true };
    private readonly Option<string[]> removeSystemPath = new("--remove-system-path") { Description = "Remove a read-only system path.", AllowMultipleArgumentsPerToken = true };

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(mode);
        command.Add(dataRoot);
        command.Add(allow);
        command.Add(disallow);
        command.Add(systemPath);
        command.Add(removeSystemPath);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var id = environment.Id!.Value;
        var current = await context.Api.GetComposePolicyAsync(id, context.CancellationToken);
        var request = new UpdateComposePolicyRequest
        {
            Mode = context.Value(mode) ?? current.Mode,
            DataRoot = context.Value(dataRoot) ?? current.DataRoot,
            Allowances = Edit(current.Allowances, context.Value(allow), context.Value(disallow)),
            SystemReadOnlyPaths = Edit(current.SystemReadOnlyPaths, context.Value(systemPath), context.Value(removeSystemPath)),
        };
        var updated = await context.Api.UpdateComposePolicyAsync(id, request, context.CancellationToken);
        context.Output.Done($"The compose policy of {environment.Name} is {updated.Mode}; allowances: {Describe(updated.Allowances)}.");
        return ExitCodes.Ok;
    }

    private static List<string> Edit(List<string>? current, string[]? add, string[]? remove) =>
        [.. (current ?? []).Concat(add ?? []).Except(remove ?? [], StringComparer.Ordinal).Distinct(StringComparer.Ordinal)];

    private static string Describe(List<string>? allowances) => allowances is { Count: > 0 } ? string.Join(", ", allowances) : "none";
}

internal sealed class PolicyReportCommand() : EnvironmentCommand("report", "Check the deployed stacks against the current compose policy.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var report = await context.Api.GetComposePolicyReportAsync(environment.Id!.Value, context.CancellationToken);
        await context.Output.ListAsync(
            report,
            items =>
            {
                var table = new Table("STACK", "STATE", "VIOLATIONS");
                foreach (var row in items)
                {
                    var state = row.Checked != true ? "not checked" : row.Violations is { Count: > 0 } ? "violates" : "follows";
                    var violations = string.Join("; ", (row.Violations ?? []).Select(v =>
                        $"{(v.Service is null ? "" : v.Service + ": ")}{v.Message} ({(v.Allowance is null ? "no allowance" : "allowance " + v.Allowance)})"));
                    table.AddRow(row.StackName, state, violations);
                }

                return table;
            },
            $"{environment.Name} has no stacks.");
        return ExitCodes.Ok;
    }
}

internal sealed class EnvRoleListCommand() : EnvironmentCommand("list", "List roles on the environment and its stacks (its administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var roles = await context.Api.ListEnvironmentRolesAsync(environment.Id!.Value, context.CancellationToken);
        await context.Output.ListAsync(
            roles,
            items =>
            {
                var table = new Table("USER", "SCOPE", "ROLE", "ID");
                foreach (var role in items)
                {
                    table.AddRow(role.UserName, role.StackName ?? "(environment)", role.Role?.ToString(), role.Id?.ToString());
                }

                return table;
            },
            $"Nobody has a role on {environment.Name}.");
        return ExitCodes.Ok;
    }
}

internal sealed class EnvRoleGrantCommand() : EnvironmentCommand("grant", "Give an existing user a role on the environment or a stack (its administrators).")
{
    private readonly Argument<string> user = new("user") { Description = "User name." };
    private readonly Option<string?> stack = new("--stack") { Description = "Stack in the environment; leave out for the whole environment." };
    private readonly Option<Role> role = new("--role")
    {
        Description = "Viewer, Operator, Editor, or EnvironmentAdmin (whole environment, administrators only).",
        Required = true,
    };

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(user);
        command.Add(stack);
        command.Add(role);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var stackId = context.Value(stack) is { } name ? (await context.Api.FindStackAsync(environment, name, context.CancellationToken)).Id : null;
        var granted = await context.Api.GrantEnvironmentRoleAsync(
            environment.Id!.Value, context.Value(user), stackId, context.Value(role), context.CancellationToken);
        context.Output.Done($"{granted.UserName} is {granted.Role} on {granted.StackName ?? environment.Name} (assignment {granted.Id}).");
        return ExitCodes.Ok;
    }
}

internal sealed class EnvRoleChangeCommand() : EnvironmentCommand("change", "Change a role on the environment (its administrators).")
{
    private readonly Argument<Guid> assignment = new("assignment") { Description = "Assignment id, see 'forest env role list'." };
    private readonly Option<Role> role = new("--role") { Description = "Viewer, Operator, Editor or EnvironmentAdmin.", Required = true };

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(assignment);
        command.Add(role);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var changed = await context.Api.ChangeEnvironmentRoleAsync(
            environment.Id!.Value, context.Value(assignment), context.Value(role), context.CancellationToken);
        context.Output.Done($"{changed.UserName} is now {changed.Role} on {changed.StackName ?? environment.Name}.");
        return ExitCodes.Ok;
    }
}

internal sealed class EnvRoleRevokeCommand() : EnvironmentCommand("revoke", "Take a role on the environment away (its administrators).")
{
    private readonly Argument<Guid> assignment = new("assignment") { Description = "Assignment id, see 'forest env role list'." };

    protected override void Configure(Command command)
    {
        base.Configure(command);
        command.Add(assignment);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        await context.Api.RevokeEnvironmentRoleAsync(environment.Id!.Value, context.Value(assignment), context.CancellationToken);
        context.Output.Done("Role revoked.");
        return ExitCodes.Ok;
    }
}
