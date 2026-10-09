using System.CommandLine;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

/// <summary>A command about one repository, named as its argument by name, address or id.</summary>
internal abstract class RepositoryCommand(string name, string description) : ForestCommand(name, description)
{
    private readonly Argument<string> repositoryArgument = new("repository") { Description = "Repository name, address or id." };

    protected override void Configure(Command command)
    {
        command.Add(repositoryArgument);
        ConfigureRepository(command);
    }

    protected sealed override async Task<int> ExecuteAsync(CommandContext context) =>
        await ExecuteAsync(context, await context.Api.FindRepositoryAsync(context.Value(repositoryArgument), context.CancellationToken));

    protected virtual void ConfigureRepository(Command command)
    {
    }

    protected abstract Task<int> ExecuteAsync(CommandContext context, RepositoryResponse repository);

    /// <summary>Shows a new webhook secret once, with the address the git server sends pushes to.</summary>
    internal static async Task ShowWebhookAsync<T>(CommandContext context, T response, string secret, Uri address)
        where T : Microsoft.Kiota.Abstractions.Serialization.IParsable
    {
        if (context.Output.IsJson)
        {
            await context.Output.ItemAsync(response, _ => []);
            return;
        }

        context.Output.Note($"Webhook address: {address}");
        context.Output.Note("Secret, shown once; paste both into the git server's webhook settings (content type application/json, push events):");
        context.Output.Secret(secret);
    }
}

internal sealed class RepoListCommand() : ForestCommand("list", "List repositories you can use.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var repositories = await context.Api.ListRepositoriesAsync(context.CancellationToken);
        await context.Output.ListAsync(
            repositories,
            items =>
            {
                var table = new Table("NAME", "URL", "CONNECTION", "STACKS", "WEBHOOK");
                foreach (var r in items)
                {
                    table.AddRow(r.Name, r.Url, r.ConnectionName ?? "public", r.StackCount?.ToString(), CliOutput.YesNo(r.HasWebhook == true || r.ConnectionHasWebhook == true));
                }

                return table;
            },
            "No repositories you can use.");
        return ExitCodes.Ok;
    }
}

internal sealed class RepoAddCommand() : ForestCommand("add", "Add a repository (Editors and administrators).")
{
    private readonly Argument<string> name = new("name") { Description = "Repository name; need not be unique." };
    private readonly Option<string> url = new("--url") { Description = "HTTPS address of the repository.", Required = true };
    private readonly Option<string?> connection = new("--connection") { Description = "Git connection for a private repository; leave out for a public one." };

    protected override void Configure(Command command)
    {
        command.Add(name);
        command.Add(url);
        command.Add(connection);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var connectionId = context.Value(connection) is { } given
            ? (await context.Api.FindGitConnectionAsync(given, context.CancellationToken)).Id
            : null;
        var repository = await context.Api.AddRepositoryAsync(context.Value(name), context.Value(url)!, connectionId, context.CancellationToken);
        context.Output.Done($"Repository {repository.Name} added ({repository.ConnectionName ?? "public"}).");
        return ExitCodes.Ok;
    }
}

internal sealed class RepoUpdateCommand() : RepositoryCommand("update", "Change a repository's name, address or connection.")
{
    private readonly Option<string?> name = new("--name") { Description = "New name." };
    private readonly Option<string?> url = new("--url") { Description = "New address." };
    private readonly Option<string?> connection = new("--connection") { Description = "Move under this git connection." };
    private readonly Option<bool> makePublic = new("--public") { Description = "Take the repository out of its connection." };

    protected override void ConfigureRepository(Command command)
    {
        command.Add(name);
        command.Add(url);
        command.Add(connection);
        command.Add(makePublic);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, RepositoryResponse repository)
    {
        var connectionId = context.Value(makePublic)
            ? null
            : context.Value(connection) is { } given
                ? (await context.Api.FindGitConnectionAsync(given, context.CancellationToken)).Id
                : repository.ConnectionId;
        var updated = await context.Api.UpdateRepositoryAsync(
            repository.Id!.Value, context.Value(name) ?? repository.Name!, context.Value(url) ?? repository.Url!, connectionId, context.CancellationToken);
        context.Output.Done($"Repository {updated.Name} updated.");
        return ExitCodes.Ok;
    }
}

internal sealed class RepoRemoveCommand() : RepositoryCommand("remove", "Remove a repository no stack deploys from.")
{
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void ConfigureRepository(Command command) => command.Add(yes);

    protected override async Task<int> ExecuteAsync(CommandContext context, RepositoryResponse repository)
    {
        Confirmation.Require(context, yes, $"remove repository {repository.Name}");
        await context.Api.RemoveRepositoryAsync(repository.Id!.Value, context.CancellationToken);
        context.Output.Done($"Repository {repository.Name} removed.");
        return ExitCodes.Ok;
    }
}

internal sealed class RepoWebhookCreateCommand() : RepositoryCommand("create", "Create the repository's push webhook secret; a new one replaces the old.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, RepositoryResponse repository)
    {
        var created = await context.Api.CreateRepositoryWebhookAsync(repository.Id!.Value, context.CancellationToken);
        await ShowWebhookAsync(context, created, created.Secret ?? "", new Uri(context.Api.Server, $"api/webhooks/{repository.Id}"));
        return ExitCodes.Ok;
    }
}

internal sealed class RepoWebhookRemoveCommand() : RepositoryCommand("remove", "Stop accepting pushes for the repository.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, RepositoryResponse repository)
    {
        await context.Api.RemoveRepositoryWebhookAsync(repository.Id!.Value, context.CancellationToken);
        context.Output.Done($"The webhook of {repository.Name} is removed.");
        return ExitCodes.Ok;
    }
}
