using System.CommandLine;
using ContainerForest.Cli.Configuration;
using ContainerForest.Cli.Secrets;

namespace ContainerForest.Cli.Commands;

/// <summary>
/// <c>forest git setup</c>: turns the forest filter on in this clone (ADR-0010). Files under <c>filter=forest</c> in
/// .gitattributes are then plain text in the work tree when the user may decrypt them, and encrypted in git.
/// </summary>
internal sealed class GitSetupCommand() : ForestCommand("setup", "Turn the forest git filter on in this clone: secrets decrypted here, encrypted in git.")
{
    private readonly Option<bool> localSops = new("--local-sops")
    {
        Description = "Decrypt with your own sops keys first and encrypt with local sops (works without the aggregator).",
    };

    public override bool NeedsConnection => false;

    protected override void Configure(Command command) => command.Add(localSops);

    protected override Task<int> ExecuteAsync(CommandContext context)
    {
        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        if (context.Value(localSops))
        {
            tree.SetConfig(GitFilterSetup.LocalSopsKey, "true");
        }

        var contextName = CurrentContextName(context);
        var (decrypted, total) = GitFilterSetup.Apply(tree, contextName, GitFilterSetup.Executable(), context.Output);
        context.Output.Done(contextName is null
            ? "The forest filter is on; there is no context yet: run 'forest login --secrets <server>'."
            : $"The forest filter is on for context '{contextName}': {decrypted} of {total} secret file(s) decrypted.");
        if (decrypted < total)
        {
            context.Output.Note(
                "Files you may not decrypt stay encrypted. To decrypt, you need the Editor role on their environment and a token " +
                "with the right to decrypt secrets ('forest login --secrets'); then run 'forest git setup' again.");
        }

        return Task.FromResult(ExitCodes.Ok);
    }

    /// <summary>The context named on the command line or in CF_CONTEXT, else the current one; null when there is none.</summary>
    internal static string? CurrentContextName(CommandContext context)
    {
        var requested = context.Value(GlobalOptions.Context) ?? context.Host.GetVariable(CredentialResolver.ContextVariable);
        return context.Host.Credentials.FindContext(context.Host.ConfigStore.Load(), requested is { Length: > 0 } ? requested : null).Name;
    }
}

/// <summary>
/// <c>forest git teardown</c>: turns the forest filter off in this clone; the secret files go back to the ciphertext
/// git has. The opposite of <c>forest git setup</c>, e.g. before the clone or the machine goes to someone else.
/// </summary>
internal sealed class GitTeardownCommand() : ForestCommand("teardown", "Turn the forest git filter off in this clone: secret files become ciphertext again.")
{
    public override bool NeedsConnection => false;

    protected override Task<int> ExecuteAsync(CommandContext context)
    {
        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        var encrypted = GitFilterSetup.Remove(tree, context.Output);
        context.Output.Done($"The forest filter is off in this clone; {encrypted} decrypted file(s) are ciphertext again.");
        context.Output.Note(
            "Plain text may still be elsewhere: stashes, copies, your editor's backups. The API token stays in the CLI " +
            "context; 'forest logout' revokes it.");
        return Task.FromResult(ExitCodes.Ok);
    }
}

/// <summary><c>forest git filter-process</c>: the filter git runs (filter.forest.process); not for people.</summary>
internal sealed class GitFilterProcessCommand() : ForestCommand("filter-process", "The git filter itself; git runs it, see 'forest git setup'.")
{
    public override bool NeedsConnection => false;

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var session = SecretsSession.Open(context.Host, context.Output.Error);
        try
        {
            var operations = new Dictionary<string, FilterOperation>
            {
                ["clean"] = new CleanOperation(session.Services),
                ["smudge"] = new SmudgeOperation(session.Services),
            };
            await new GitFilterProcess(context.Host.StandardInput, context.Host.StandardOutput, operations).RunAsync(context.CancellationToken);
            return ExitCodes.Ok;
        }
        catch (InvalidDataException exception)
        {
            throw new CliException(exception.Message, ExitCodes.Usage);
        }
        finally
        {
            session.Services.Cache?.Save();
        }
    }
}

/// <summary>
/// <c>forest git textconv &lt;file&gt;</c>: what git diff shows for a file under the filter (diff.forest.textconv). An
/// encrypted file is shown decrypted when the user may decrypt it, as it is otherwise.
/// </summary>
internal sealed class GitTextconvCommand() : ForestCommand("textconv", "Print a file decrypted for git diff; git runs it, see 'forest git setup'.")
{
    private readonly Argument<string> file = new("file") { Description = "The file git passes, usually a temporary copy." };

    public override bool NeedsConnection => false;

    protected override void Configure(Command command) => command.Add(file);

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var path = context.Value(file);
        var content = await File.ReadAllBytesAsync(path, context.CancellationToken);
        var session = SecretsSession.Open(context.Host, context.Output.Error, useCache: false);

        // Only the name matters: it tells the format, and git keeps the file's name in its temporary copies.
        var result = await new SmudgeOperation(session.Services).RunAsync(Path.GetFileName(path), content, context.CancellationToken);
        await context.Host.StandardOutput.WriteAsync(result.Content, context.CancellationToken);
        await context.Host.StandardOutput.FlushAsync(context.CancellationToken);
        return ExitCodes.Ok;
    }
}
