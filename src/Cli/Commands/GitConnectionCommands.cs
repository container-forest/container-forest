using System.CommandLine;
using ContainerForest.Cli.Api;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

/// <summary>A command about one git connection (ADR-0006), named as its argument.</summary>
internal abstract class GitConnectionCommand(string name, string description) : ForestCommand(name, description)
{
    private readonly Argument<string> connectionArgument = new("connection") { Description = "Connection name or id." };

    protected override void Configure(Command command)
    {
        command.Add(connectionArgument);
        ConfigureConnection(command);
    }

    protected sealed override async Task<int> ExecuteAsync(CommandContext context) =>
        await ExecuteAsync(context, await context.Api.FindGitConnectionAsync(context.Value(connectionArgument), context.CancellationToken));

    protected virtual void ConfigureConnection(Command command)
    {
    }

    protected abstract Task<int> ExecuteAsync(CommandContext context, GitConnectionResponse connection);
}

/// <summary>Which environments a connection is allowed in: <c>--all-envs</c> or one <c>--env</c> per environment.</summary>
internal sealed class ConnectionEnvironmentOptions
{
    public Option<string[]> Environments { get; } = new("--env", "-e")
    {
        Description = "An environment that may deploy from the connection's repositories; repeat for more.",
        AllowMultipleArgumentsPerToken = false,
    };

    public Option<bool> All { get; } = new("--all-envs") { Description = "Every environment, present and future." };

    public void AddTo(Command command)
    {
        command.Add(Environments);
        command.Add(All);
    }

    /// <summary>Null for every environment; the ids otherwise; <paramref name="current"/> when neither option is given.</summary>
    public async Task<IReadOnlyCollection<Guid>?> ResolveAsync(CommandContext context, Func<IReadOnlyCollection<Guid>?>? current)
    {
        var names = context.Value(Environments) ?? [];
        if (context.Value(All))
        {
            return names.Length == 0 ? null : throw new CliException("Choose --all-envs or --env, not both.", ExitCodes.Usage);
        }

        if (names.Length == 0)
        {
            return current is not null
                ? current()
                : throw new CliException("Choose the environments with --env (repeat for more) or --all-envs.", ExitCodes.Usage);
        }

        var ids = new List<Guid>();
        foreach (var name in names)
        {
            ids.Add((await context.Api.ResolveEnvironmentAsync(name, null, context.CancellationToken)).Id!.Value);
        }

        return ids;
    }
}

internal sealed class ConnectionListCommand() : ForestCommand("list", "List git connections (Editors see those of their environments).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var connections = await context.Api.ListGitConnectionsAsync(context.CancellationToken);
        var environments = context.Output.IsJson ? [] : await context.Api.ListEnvironmentsAsync(context.CancellationToken);
        await context.Output.ListAsync(
            connections,
            items =>
            {
                var table = new Table("NAME", "PREFIX", "TOKEN", "ENVIRONMENTS", "REPOS", "WEBHOOK");
                foreach (var c in items)
                {
                    var allowed = c.AllEnvironments == true
                        ? "all"
                        : string.Join(",", environments.Where(e => c.EnvironmentIds?.Contains(e.Id) == true).Select(e => e.Name));
                    table.AddRow(c.Name, c.UrlPrefix, c.HasAccessToken == true ? c.AccessUserName ?? "yes" : "no", allowed, c.RepositoryCount?.ToString(), CliOutput.YesNo(c.HasWebhook));
                }

                return table;
            },
            "No git connections you can see.");
        return ExitCodes.Ok;
    }
}

internal sealed class ConnectionAddCommand() : ForestCommand("add", "Add a git connection (administrators).")
{
    private readonly Argument<string> name = new("name") { Description = "Unique connection name." };
    private readonly Option<string> prefix = new("--prefix") { Description = "Address prefix, e.g. https://gitea.example.com/team/.", Required = true };
    private readonly Option<bool> withToken = new("--with-token") { Description = "Read a read-only access token (typed or from standard input)." };
    private readonly Option<string?> tokenUser = new("--token-user") { Description = "User name sent with the token; leave out for the default." };
    private readonly ConnectionEnvironmentOptions environments = new();

    protected override void Configure(Command command)
    {
        command.Add(name);
        command.Add(prefix);
        environments.AddTo(command);
        command.Add(withToken);
        command.Add(tokenUser);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var allowed = await environments.ResolveAsync(context, null);
        var token = context.Value(withToken) ? await SecretInput.ReadAsync(context, "Access token") : null;
        var connection = await context.Api.AddGitConnectionAsync(
            context.Value(name), context.Value(prefix)!, allowed, context.Value(tokenUser), token, context.CancellationToken);
        context.Output.Done($"Git connection {connection.Name} added for {connection.UrlPrefix}.");
        return ExitCodes.Ok;
    }
}

internal sealed class ConnectionUpdateCommand() : GitConnectionCommand("update", "Change a connection's name, prefix or environments (administrators).")
{
    private readonly Option<string?> name = new("--name") { Description = "New name." };
    private readonly Option<string?> prefix = new("--prefix") { Description = "New address prefix." };
    private readonly ConnectionEnvironmentOptions environments = new();

    protected override void ConfigureConnection(Command command)
    {
        command.Add(name);
        command.Add(prefix);
        environments.AddTo(command);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, GitConnectionResponse connection)
    {
        var allowed = await environments.ResolveAsync(
            context, () => connection.AllEnvironments == true ? null : (connection.EnvironmentIds ?? []).OfType<Guid>().ToList());
        var updated = await context.Api.UpdateGitConnectionAsync(
            connection.Id!.Value, context.Value(name) ?? connection.Name!, context.Value(prefix) ?? connection.UrlPrefix!, allowed, context.CancellationToken);
        context.Output.Done($"Git connection {updated.Name} updated.");
        return ExitCodes.Ok;
    }
}

internal sealed class ConnectionRemoveCommand() : GitConnectionCommand("remove", "Remove a connection without repositories (administrators).")
{
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void ConfigureConnection(Command command) => command.Add(yes);

    protected override async Task<int> ExecuteAsync(CommandContext context, GitConnectionResponse connection)
    {
        Confirmation.Require(context, yes, $"remove git connection {connection.Name} and its token");
        await context.Api.RemoveGitConnectionAsync(connection.Id!.Value, context.CancellationToken);
        context.Output.Done($"Git connection {connection.Name} removed.");
        return ExitCodes.Ok;
    }
}

internal sealed class ConnectionSetTokenCommand() : GitConnectionCommand("set-token", "Set the connection's read-only access token, typed or from standard input (administrators).")
{
    private readonly Option<string?> tokenUser = new("--token-user") { Description = "User name sent with the token; leave out for the default." };

    protected override void ConfigureConnection(Command command) => command.Add(tokenUser);

    protected override async Task<int> ExecuteAsync(CommandContext context, GitConnectionResponse connection)
    {
        var token = await SecretInput.ReadAsync(context, "Access token");
        await context.Api.SetGitConnectionTokenAsync(connection.Id!.Value, context.Value(tokenUser), token, context.CancellationToken);
        context.Output.Done($"Access token of {connection.Name} saved.");
        return ExitCodes.Ok;
    }
}

internal sealed class ConnectionRemoveTokenCommand() : GitConnectionCommand("remove-token", "Fetch the connection's repositories anonymously from now on (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, GitConnectionResponse connection)
    {
        await context.Api.RemoveGitConnectionTokenAsync(connection.Id!.Value, context.CancellationToken);
        context.Output.Done($"Access token of {connection.Name} removed.");
        return ExitCodes.Ok;
    }
}

internal sealed class ConnectionWebhookCreateCommand() : GitConnectionCommand("create", "Create the organization webhook secret of the connection (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, GitConnectionResponse connection)
    {
        var created = await context.Api.CreateGitConnectionWebhookAsync(connection.Id!.Value, context.CancellationToken);
        await RepositoryCommand.ShowWebhookAsync(
            context, created, created.Secret ?? "", new Uri(context.Api.Server, $"api/webhooks/connections/{connection.Id}"));
        return ExitCodes.Ok;
    }
}

internal sealed class ConnectionWebhookRemoveCommand() : GitConnectionCommand("remove", "Stop accepting organization pushes for the connection (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, GitConnectionResponse connection)
    {
        await context.Api.RemoveGitConnectionWebhookAsync(connection.Id!.Value, context.CancellationToken);
        context.Output.Done($"The webhook of {connection.Name} is removed.");
        return ExitCodes.Ok;
    }
}
