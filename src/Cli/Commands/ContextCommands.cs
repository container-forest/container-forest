using System.CommandLine;
using System.Text.Json;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

/// <summary><c>forest context list</c>: the saved servers, which one is current; never the tokens.</summary>
internal sealed class ContextListCommand() : ForestCommand("list", "List saved contexts.")
{
    public override bool NeedsConnection => false;

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var config = context.Host.ConfigStore.Load();
        var contexts = config.Contexts.OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
        if (context.Output.IsJson)
        {
            using var buffer = new MemoryStream();
            await using var json = new Utf8JsonWriter(buffer);
            json.WriteStartArray();
            foreach (var (name, entry) in contexts)
            {
                json.WriteStartObject();
                json.WriteString("name", name);
                json.WriteString("url", entry.Url);
                json.WriteString("userName", entry.UserName);
                json.WriteBoolean("loggedIn", entry.Token is not null);
                json.WriteString("environment", entry.Environment);
                json.WriteBoolean("current", name == config.CurrentContext);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            await json.FlushAsync(context.CancellationToken);
            await context.Output.Out.WriteLineAsync(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
            return ExitCodes.Ok;
        }

        if (contexts.Count == 0)
        {
            context.Output.Note("No contexts yet; run 'forest login <address>'.");
            return ExitCodes.Ok;
        }

        var table = new Table("", "NAME", "SERVER", "USER", "ENVIRONMENT");
        foreach (var (name, entry) in contexts)
        {
            table.AddRow(name == config.CurrentContext ? "*" : " ", name, entry.Url, entry.Token is null ? "(logged out)" : entry.UserName, entry.Environment);
        }

        table.Render(context.Output.Out);
        return ExitCodes.Ok;
    }
}

/// <summary><c>forest context use NAME</c>: makes a saved context the current one.</summary>
internal sealed class ContextUseCommand() : ForestCommand("use", "Make a context the current one.")
{
    private readonly Argument<string> name = new("name") { Description = "Context name, see 'forest context list'." };

    public override bool NeedsConnection => false;

    protected override void Configure(Command command) => command.Add(name);

    protected override Task<int> ExecuteAsync(CommandContext context)
    {
        var store = context.Host.ConfigStore;
        var config = store.Load();
        var (found, _) = context.Host.Credentials.FindContext(config, context.Value(name));
        config.CurrentContext = found;
        store.Save(config);
        context.Output.Done($"Now using context '{found}'.");
        return Task.FromResult(ExitCodes.Ok);
    }
}

/// <summary><c>forest context remove NAME</c>: forgets a context. The token stays valid; <c>logout</c> revokes it.</summary>
internal sealed class ContextRemoveCommand() : ForestCommand("remove", "Forget a context without revoking its token.")
{
    private readonly Argument<string> name = new("name") { Description = "Context name." };

    public override bool NeedsConnection => false;

    protected override void Configure(Command command) => command.Add(name);

    protected override Task<int> ExecuteAsync(CommandContext context)
    {
        var store = context.Host.ConfigStore;
        var config = store.Load();
        var (found, entry) = context.Host.Credentials.FindContext(config, context.Value(name));
        config.Contexts.Remove(found!);
        if (config.CurrentContext == found)
        {
            config.CurrentContext = null;
        }

        store.Save(config);
        if (entry?.Token is not null)
        {
            context.Output.Note("The token is still valid on the server; revoke it in the panel (API tokens) if it is not needed.");
        }

        context.Output.Done($"Context '{found}' removed.");
        return Task.FromResult(ExitCodes.Ok);
    }
}

/// <summary><c>forest context set-env ENV</c>: the environment commands use without <c>--env</c>.</summary>
internal sealed class ContextSetEnvironmentCommand() : ForestCommand("set-env", "Set the context's default environment.")
{
    private readonly Argument<string?> environment = new("environment")
    {
        Description = "Environment name or id; leave out with --clear.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private readonly Option<bool> clear = new("--clear") { Description = "Remove the default environment." };

    protected override void Configure(Command command)
    {
        command.Add(environment);
        command.Add(clear);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var contextName = context.Connection.ContextName
            ?? throw new CliException("There is no context to change; run 'forest login' first.", ExitCodes.Usage);
        EnvironmentResponse? chosen = null;
        if (!context.Value(clear))
        {
            var requested = context.Value(environment) ?? throw new CliException("Name an environment, or use --clear.", ExitCodes.Usage);
            chosen = await context.Api.ResolveEnvironmentAsync(requested, null, context.CancellationToken);
        }

        var store = context.Host.ConfigStore;
        var config = store.Load();
        config.Contexts[contextName].Environment = chosen?.Name;
        store.Save(config);
        context.Output.Done(chosen is null
            ? $"Context '{contextName}' has no default environment now."
            : $"Context '{contextName}' now uses environment {chosen.Name} by default.");
        return ExitCodes.Ok;
    }
}
