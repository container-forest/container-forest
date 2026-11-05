using System.CommandLine;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

/// <summary>A command about one user (administrators only), named as its argument by name or id.</summary>
internal abstract class UserCommand(string name, string description) : ForestCommand(name, description)
{
    private readonly Argument<string> userArgument = new("user") { Description = "User name or id." };

    protected override void Configure(Command command)
    {
        command.Add(userArgument);
        ConfigureUser(command);
    }

    protected sealed override async Task<int> ExecuteAsync(CommandContext context) =>
        await ExecuteAsync(context, await context.Api.FindUserAsync(context.Value(userArgument), context.CancellationToken));

    protected virtual void ConfigureUser(Command command)
    {
    }

    protected abstract Task<int> ExecuteAsync(CommandContext context, UserResponse user);

    internal static string Status(UserResponse user, DateTimeOffset now) =>
        user.IsActive != true ? "deactivated" : user.LockedOutUntil is { } until && until > now ? $"locked until {CliOutput.Time(until)}" : "active";
}

internal sealed class UserListCommand() : ForestCommand("list", "List users (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var users = await context.Api.ListUsersAsync(context.CancellationToken);
        var now = context.Host.Time.GetUtcNow();
        await context.Output.ListAsync(
            users,
            items =>
            {
                var table = new Table("NAME", "ADMIN", "STATUS", "CREATED");
                foreach (var u in items)
                {
                    table.AddRow(u.UserName, CliOutput.YesNo(u.IsAdmin), UserCommand.Status(u, now), CliOutput.Time(u.CreatedAt));
                }

                return table;
            },
            "No users.");
        return ExitCodes.Ok;
    }
}

internal sealed class UserShowCommand() : UserCommand("show", "Show a user and their roles (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        var details = await context.Api.GetUserAsync(user.Id!.Value, context.CancellationToken);
        if (context.Output.IsJson)
        {
            await context.Output.ItemAsync(details, _ => []);
            return ExitCodes.Ok;
        }

        await context.Output.ItemAsync(user, u =>
        [
            ("Name", u.UserName),
            ("Id", u.Id?.ToString()),
            ("Administrator", CliOutput.YesNo(u.IsAdmin)),
            ("Status", Status(u, context.Host.Time.GetUtcNow())),
            ("Created", CliOutput.Time(u.CreatedAt)),
        ]);
        var roles = details.RoleAssignments ?? [];
        await context.Output.Out.WriteLineAsync();
        if (roles.Count == 0)
        {
            await context.Output.Out.WriteLineAsync("No roles.");
            return ExitCodes.Ok;
        }

        var table = new Table("ROLE", "ENVIRONMENT", "STACK", "GRANTED", "ID");
        foreach (var r in roles)
        {
            table.AddRow(r.Role?.ToString(), r.EnvironmentName, r.StackName ?? "(whole environment)", CliOutput.Time(r.GrantedAt), r.Id?.ToString());
        }

        table.Render(context.Output.Out);
        return ExitCodes.Ok;
    }
}

internal sealed class UserCreateCommand() : ForestCommand("create", "Create a user; the initial password is typed or read from standard input (administrators).")
{
    private readonly Argument<string> name = new("name") { Description = "User name." };
    private readonly Option<bool> admin = new("--admin") { Description = "Make the user an administrator." };

    protected override void Configure(Command command)
    {
        command.Add(name);
        command.Add(admin);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var password = await SecretInput.ReadAsync(context, "Initial password");
        var user = await context.Api.CreateUserAsync(context.Value(name), password, context.Value(admin), context.CancellationToken);
        context.Output.Done($"User {user.UserName} created{(user.IsAdmin == true ? " as an administrator" : "")}; hand the password over yourself.");
        return ExitCodes.Ok;
    }
}

internal sealed class UserRenameCommand() : UserCommand("rename", "Rename a user (administrators).")
{
    private readonly Argument<string> newName = new("new-name") { Description = "New user name." };

    protected override void ConfigureUser(Command command) => command.Add(newName);

    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        await context.Api.RenameUserAsync(user.Id!.Value, context.Value(newName), context.CancellationToken);
        context.Output.Done($"User {user.UserName} is now {context.Value(newName)}.");
        return ExitCodes.Ok;
    }
}

/// <summary><c>forest user activate|deactivate NAME</c>.</summary>
internal sealed class UserActivationCommand(bool activate)
    : UserCommand(activate ? "activate" : "deactivate", activate ? "Let a deactivated user in again (administrators)." : "Deactivate a user: no sign-in, no tokens, no bot (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        await context.Api.SetUserActiveAsync(user.Id!.Value, activate, context.CancellationToken);
        context.Output.Done($"User {user.UserName} {(activate ? "activated" : "deactivated")}.");
        return ExitCodes.Ok;
    }
}

internal sealed class UserSetAdminCommand() : UserCommand("set-admin", "Grant or take away administrator rights (administrators).")
{
    private readonly Argument<string> state = new("state") { Description = "on or off." };

    protected override void ConfigureUser(Command command)
    {
        state.AcceptOnlyFromAmong("on", "off");
        command.Add(state);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        var isAdmin = context.Value(state) == "on";
        await context.Api.SetUserAdminAsync(user.Id!.Value, isAdmin, context.CancellationToken);
        context.Output.Done($"User {user.UserName} is {(isAdmin ? "now" : "no longer")} an administrator.");
        return ExitCodes.Ok;
    }
}

internal sealed class UserResetPasswordCommand() : UserCommand("reset-password", "Set a new password, typed or from standard input; ends the user's sessions (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        var password = await SecretInput.ReadAsync(context, "New password");
        await context.Api.ResetUserPasswordAsync(user.Id!.Value, password, context.CancellationToken);
        context.Output.Done($"Password of {user.UserName} changed; their sessions have ended.");
        return ExitCodes.Ok;
    }
}

internal sealed class UserUnlockCommand() : UserCommand("unlock", "Lift a lockout after failed sign-ins (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        await context.Api.UnlockUserAsync(user.Id!.Value, context.CancellationToken);
        context.Output.Done($"User {user.UserName} unlocked.");
        return ExitCodes.Ok;
    }
}

internal sealed class RoleGrantCommand() : UserCommand("grant", "Give a user a role on an environment or a stack (administrators).")
{
    private readonly Option<string> environment = new("--env", "-e") { Description = "Environment name or id.", Required = true };
    private readonly Option<string?> stack = new("--stack") { Description = "Stack in the environment; leave out for the whole environment." };
    private readonly Option<Role> role = new("--role")
    {
        Description = "Viewer, Operator, Editor, or EnvironmentAdmin (on a whole environment only).",
        Required = true,
    };

    protected override void ConfigureUser(Command command)
    {
        command.Add(environment);
        command.Add(stack);
        command.Add(role);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        var env = await context.Api.ResolveEnvironmentAsync(context.Value(environment), null, context.CancellationToken);
        var stackId = context.Value(stack) is { } stackName
            ? (await context.Api.FindStackAsync(env, stackName, context.CancellationToken)).Id
            : null;
        var granted = await context.Api.GrantRoleAsync(user.Id!.Value, env.Id!.Value, stackId, context.Value(role), context.CancellationToken);
        context.Output.Done($"{user.UserName} is {granted.Role} on {granted.StackName ?? granted.EnvironmentName} (assignment {granted.Id}).");
        return ExitCodes.Ok;
    }
}

internal sealed class RoleChangeCommand() : UserCommand("change", "Change the role of an assignment (administrators).")
{
    private readonly Argument<Guid> assignment = new("assignment") { Description = "Assignment id, see 'forest user show'." };
    private readonly Option<Role> role = new("--role")
    {
        Description = "Viewer, Operator, Editor, or EnvironmentAdmin (on a whole environment only).",
        Required = true,
    };

    protected override void ConfigureUser(Command command)
    {
        command.Add(assignment);
        command.Add(role);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        var changed = await context.Api.ChangeRoleAsync(user.Id!.Value, context.Value(assignment), context.Value(role), context.CancellationToken);
        context.Output.Done($"{user.UserName} is now {changed.Role} on {changed.StackName ?? changed.EnvironmentName}.");
        return ExitCodes.Ok;
    }
}

internal sealed class RoleRevokeCommand() : UserCommand("revoke", "Take a role away (administrators).")
{
    private readonly Argument<Guid> assignment = new("assignment") { Description = "Assignment id, see 'forest user show'." };

    protected override void ConfigureUser(Command command) => command.Add(assignment);

    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        await context.Api.RevokeRoleAsync(user.Id!.Value, context.Value(assignment), context.CancellationToken);
        context.Output.Done($"Role revoked from {user.UserName}.");
        return ExitCodes.Ok;
    }
}

internal sealed class UserTokensCommand() : UserCommand("list", "List a user's API tokens (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        var tokens = await context.Api.ListUserTokensAsync(user.Id!.Value, context.CancellationToken);
        await context.Output.ListAsync(
            tokens,
            items =>
            {
                var table = new Table("NAME", "HINT", "ACCESS", "EXPIRES", "LAST USED", "ID");
                foreach (var t in items)
                {
                    var access = (t.ReadOnly == true ? "read-only" : "full") + (t.EnvironmentIds is null ? "" : $", {t.EnvironmentIds.Count} env");
                    table.AddRow(t.Name, t.Hint, access, t.Expired == true ? "expired" : t.ExpiresAt is null ? "never" : CliOutput.Time(t.ExpiresAt), CliOutput.Time(t.LastUsedAt), t.Id?.ToString());
                }

                return table;
            },
            $"{user.UserName} has no API tokens.");
        return ExitCodes.Ok;
    }
}

internal sealed class UserRevokeTokenCommand() : UserCommand("revoke", "Revoke a user's API token (administrators).")
{
    private readonly Argument<Guid> token = new("token") { Description = "Token id, see 'forest user token list'." };

    protected override void ConfigureUser(Command command) => command.Add(token);

    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        await context.Api.RevokeUserTokenAsync(user.Id!.Value, context.Value(token), context.CancellationToken);
        context.Output.Done($"Token of {user.UserName} revoked.");
        return ExitCodes.Ok;
    }
}

internal sealed class UserPasskeysCommand() : UserCommand("list", "List a user's passkeys (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        var passkeys = await context.Api.ListUserPasskeysAsync(user.Id!.Value, context.CancellationToken);
        await context.Output.ListAsync(
            passkeys,
            items =>
            {
                var table = new Table("NAME", "ADDED", "SYNCED", "ID");
                foreach (var p in items)
                {
                    table.AddRow(p.Name, CliOutput.Time(p.CreatedAt), CliOutput.YesNo(p.IsBackedUp), p.Id);
                }

                return table;
            },
            $"{user.UserName} has no passkeys.");
        return ExitCodes.Ok;
    }
}

internal sealed class UserRemovePasskeyCommand() : UserCommand("remove", "Remove a user's passkey, e.g. of a lost device (administrators).")
{
    private readonly Argument<string> passkey = new("passkey") { Description = "Passkey id, see 'forest user passkey list'." };
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void ConfigureUser(Command command)
    {
        command.Add(passkey);
        command.Add(yes);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        Confirmation.Require(context, yes, $"remove a passkey of {user.UserName}; it will no longer sign them in");
        await context.Api.RemoveUserPasskeyAsync(user.Id!.Value, context.Value(passkey), context.CancellationToken);
        context.Output.Done($"Passkey of {user.UserName} removed.");
        return ExitCodes.Ok;
    }
}

internal sealed class UserSigningKeysCommand() : UserCommand("list", "List a user's signing keys (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        var keys = await context.Api.ListUserSigningKeysAsync(user.Id!.Value, context.CancellationToken);
        var environments = context.Output.IsJson ? [] : await context.Api.ListEnvironmentsAsync(context.CancellationToken);
        await context.Output.ListAsync(
            keys,
            items =>
            {
                var table = new Table("NAME", "FINGERPRINT", "ENVIRONMENTS", "ADDED BY", "LAST USED", "ID");
                foreach (var k in items)
                {
                    var scope = k.EnvironmentIds is null
                        ? "all"
                        : string.Join(",", environments.Where(e => k.EnvironmentIds.Contains(e.Id)).Select(e => e.Name));
                    table.AddRow(k.Name, k.Fingerprint, scope, k.AddedBy ?? user.UserName, CliOutput.Time(k.LastUsedAt), k.Id?.ToString());
                }

                return table;
            },
            $"{user.UserName} has no signing keys.");
        return ExitCodes.Ok;
    }
}

/// <summary>
/// <c>forest user signing-key add USER NAME KEY.pub [--env E]…</c>: a key for an account without a session, such as
/// CI (ADR-0011). People add their own keys in the panel.
/// </summary>
internal sealed class UserAddSigningKeyCommand() : UserCommand("add", "Add a signing key for a user, e.g. a CI account (administrators).")
{
    private readonly Argument<string> name = new("name") { Description = "Name of the key, e.g. the machine it lives on." };
    private readonly Argument<string> file = new("public-key-file") { Description = "The .pub file; '-' reads it from standard input." };
    private readonly Option<string[]> environments = new("--env", "-e")
    {
        Description = "An environment the key endorses deployments in; repeat for more. Without it, every environment.",
        AllowMultipleArgumentsPerToken = false,
    };

    protected override void ConfigureUser(Command command)
    {
        command.Add(name);
        command.Add(file);
        command.Add(environments);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        var path = context.Value(file);
        var publicKey = path == "-"
            ? await new StreamReader(context.Host.StandardInput).ReadToEndAsync(context.CancellationToken)
            : await File.ReadAllTextAsync(path, context.CancellationToken);
        List<Guid?>? ids = null;
        if (context.Value(environments) is { Length: > 0 } names)
        {
            ids = [];
            foreach (var environment in names)
            {
                ids.Add((await context.Api.ResolveEnvironmentAsync(environment, null, context.CancellationToken)).Id);
            }
        }

        var key = await context.Api.AddUserSigningKeyAsync(user.Id!.Value, context.Value(name), publicKey.Trim(), ids, context.CancellationToken);
        context.Output.Done($"Signing key {key.Fingerprint} added for {user.UserName}{(ids is null ? "" : $", limited to {ids.Count} environment(s)")}.");
        return ExitCodes.Ok;
    }
}

internal sealed class UserRemoveSigningKeyCommand() : UserCommand("remove", "Remove a user's signing key (administrators).")
{
    private readonly Argument<Guid> key = new("key") { Description = "Key id, see 'forest user signing-key list'." };
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void ConfigureUser(Command command)
    {
        command.Add(key);
        command.Add(yes);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, UserResponse user)
    {
        Confirmation.Require(context, yes, $"remove a signing key of {user.UserName}; commits signed with it stop deploying");
        await context.Api.RemoveUserSigningKeyAsync(user.Id!.Value, context.Value(key), context.CancellationToken);
        context.Output.Done($"Signing key of {user.UserName} removed.");
        return ExitCodes.Ok;
    }
}
