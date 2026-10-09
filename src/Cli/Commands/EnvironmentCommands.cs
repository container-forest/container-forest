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
                var table = new Table("ID", "RECIPIENT", "CREATED");
                foreach (var key in items)
                {
                    table.AddRow(key.Id?.ToString(), key.Recipient, CliOutput.Time(key.CreatedAt));
                }

                return table;
            },
            $"{environment.Name} has no age keys.");
        return ExitCodes.Ok;
    }
}

internal sealed class KeyCreateCommand() : EnvironmentCommand("create", "Create a new age key for the environment (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment)
    {
        var key = await context.Api.CreateAgeKeyAsync(environment.Id!.Value, context.CancellationToken);
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
