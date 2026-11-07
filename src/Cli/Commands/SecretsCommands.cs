using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;
using ContainerForest.Cli.Secrets;

namespace ContainerForest.Cli.Commands;

/// <summary>The .gitattributes lines the secrets commands write.</summary>
internal static class SecretsFiles
{
    public const string Attributes = ".gitattributes";
    public const string SopsConfig = ".sops.yaml";

    /// <summary>
    /// eol=lf: git converts line endings before smudge, and with core.autocrlf (the default of Git for Windows) the
    /// ciphertext would reach sops with CRLF, which it cannot read. eol=lf keeps LF in the work tree and in git.
    /// </summary>
    public const string FilterAttributes = "filter=forest diff=forest eol=lf";

    /// <summary>What versions before eol=lf wrote; still recognised as the commands' own lines.</summary>
    public const string LegacyFilterAttributes = "filter=forest diff=forest";

    /// <summary>Default patterns: dotenv files and anything in a secrets directory, at any depth.</summary>
    public static readonly string[] DefaultPatterns = [".env", "**/secrets/**"];

    /// <summary>
    /// Appends the lines .gitattributes does not have yet, after giving eol=lf to the forest lines it has; returns how
    /// many lines were added.
    /// </summary>
    public static int AddAttributes(GitWorkTree tree, IEnumerable<string> patterns, string comment)
    {
        var path = Path.Combine(tree.Root, Attributes);
        var original = File.Exists(path) ? File.ReadAllText(path) : "";
        var (existing, _) = WithLineEndings(original);
        var present = existing.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        var lines = patterns.Select(p => $"{p} {FilterAttributes}").Where(l => !present.Contains(l)).ToList();
        if (lines.Count == 0)
        {
            if (existing != original)
            {
                File.WriteAllText(path, existing);
            }

            return 0;
        }

        var text = new StringBuilder(existing);
        if (existing.Length > 0 && !existing.EndsWith('\n'))
        {
            text.Append('\n');
        }

        text.Append("# ").Append(comment).Append('\n');
        foreach (var line in lines)
        {
            text.Append(line).Append('\n');
        }

        File.WriteAllText(path, text.ToString());
        return lines.Count;
    }

    /// <exception cref="CliException">The filter is not set up in this clone.</exception>
    public static void EnsureFilterSetUp(GitWorkTree tree)
    {
        if (tree.GetConfig("filter.forest.process") is null)
        {
            throw new CliException("The forest filter is not on in this clone: run 'forest git setup' first.", ExitCodes.Usage);
        }
    }

    /// <summary>
    /// .gitattributes text with eol=lf added to each line that puts files under the forest filter and says nothing of
    /// line endings itself (eol=, -text or binary); returns how many lines changed. Comments and other lines stay.
    /// </summary>
    public static (string Text, int Changed) WithLineEndings(string text)
    {
        var filter = $"filter={GitWorkTree.FilterDriver}";
        var lines = text.Split('\n');
        var changed = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var body = lines[i].TrimEnd('\r');
            var tokens = body.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || tokens[0].StartsWith('#') || !tokens.Contains(filter)
                || tokens.Skip(1).Any(t => t.StartsWith("eol=", StringComparison.Ordinal) || t is "-text" or "binary"))
            {
                continue;
            }

            lines[i] = body.TrimEnd() + " eol=lf" + lines[i][body.Length..];
            changed++;
        }

        return (string.Join('\n', lines), changed);
    }

    /// <summary>
    /// Of the files under the filter, those git may convert to CRLF in the work tree: .gitattributes gives them
    /// neither eol=lf nor -text, so core.autocrlf decides, and the ciphertext reaches sops with CR.
    /// </summary>
    public static IReadOnlyList<string> WithoutLineEndings(GitWorkTree tree, IReadOnlyList<string> filtered)
    {
        var textUnset = tree.Attribute(filtered, "text").Where(a => a.Value == "unset").Select(a => a.Path).ToHashSet(StringComparer.Ordinal);
        return tree.Attribute(filtered, "eol").Where(a => a.Value != "lf" && !textUnset.Contains(a.Path)).Select(a => a.Path).ToList();
    }

    /// <summary>Notes the files under the filter that .gitattributes does not keep in LF, with what to do.</summary>
    public static void NoteLineEndings(GitWorkTree tree, IReadOnlyList<string> filtered, CliOutput output)
    {
        var exposed = WithoutLineEndings(tree, filtered);
        if (exposed.Count > 0)
        {
            output.Note(
                $"{exposed.Count} secret file(s) have no eol=lf in .gitattributes ({exposed[0]}{(exposed.Count > 1 ? ", …" : "")}): " +
                "with core.autocrlf (Git for Windows) git gives them CRLF, which sops cannot read. Run 'forest secrets fix-eol' " +
                "and commit .gitattributes.");
        }
    }

    /// <summary>
    /// Takes files out from under the filter: drops the line 'forest secrets add' or 'adopt' wrote for a file, and
    /// for a file a pattern covers, adds a line that unsets the filter for it. Puts .gitattributes back when a file is
    /// still under the filter afterwards (a .gitattributes deeper in the tree sets it).
    /// </summary>
    /// <exception cref="CliException">A file is still under the filter.</exception>
    public static void RemoveAttributes(GitWorkTree tree, IReadOnlyList<string> paths, string comment)
    {
        var path = Path.Combine(tree.Root, Attributes);
        var original = File.Exists(path) ? File.ReadAllText(path) : "";
        var own = paths.SelectMany(p => new[] { $"/{p} {FilterAttributes}", $"/{p} {LegacyFilterAttributes}" }).ToHashSet(StringComparer.Ordinal);
        var lines = original.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        lines.RemoveAll(l => own.Contains(l.Trim()));
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.Append(line).Append('\n');
        }

        File.WriteAllText(path, text.ToString());
        var covered = tree.Filtered(paths);
        if (covered.Count > 0)
        {
            text.Append("# ").Append(comment).Append('\n');
            foreach (var file in covered)
            {
                text.Append('/').Append(file).Append(" -filter -diff\n");
            }

            File.WriteAllText(path, text.ToString());
        }

        var still = tree.Filtered(paths);
        if (still.Count > 0)
        {
            File.WriteAllText(path, original);
            throw new CliException(
                $"{string.Join(", ", still)}: another .gitattributes puts it under the filter; edit that one yourself.", ExitCodes.Failed);
        }
    }

    /// <summary>
    /// The environments you can see, by the age public keys they have; a stack's own key (ADR-0012) as
    /// "environment/stack".
    /// </summary>
    public static async Task<Dictionary<string, string>> EnvironmentsByRecipientAsync(CommandContext context)
    {
        var environmentsByRecipient = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var environment in await context.Api.ListEnvironmentsAsync(context.CancellationToken))
        {
            var keys = await context.Api.ListAgeKeysAsync(environment.Id!.Value, context.CancellationToken);
            var stacks = keys.Any(k => k.StackId is not null)
                ? (await context.Api.ListStacksAsync(environment.Id!.Value, context.CancellationToken)).ToDictionary(s => s.Id!.Value, s => s.Name!)
                : [];
            foreach (var key in keys)
            {
                environmentsByRecipient[key.Recipient!] = key.StackId is { } stackId
                    ? $"{environment.Name}/{stacks.GetValueOrDefault(stackId, stackId.ToString())}"
                    : environment.Name!;
            }
        }

        return environmentsByRecipient;
    }

    /// <summary>"prod" or "prod/web": an environment's key, or the own key of one of its stacks (ADR-0012).</summary>
    public static async Task<(string Name, string Recipient)> ResolveKeyAsync(CommandContext context, string target)
    {
        var slash = target.IndexOf('/', StringComparison.Ordinal);
        var environment = await context.Api.ResolveEnvironmentAsync(slash < 0 ? target : target[..slash], null, context.CancellationToken);
        if (slash < 0)
        {
            return (environment.Name!, await SecretsInitCommand.RecipientAsync(context, environment));
        }

        var stack = await context.Api.FindStackAsync(environment, target[(slash + 1)..], context.CancellationToken);
        return ($"{environment.Name}/{stack.Name}", await SecretsInitCommand.RecipientAsync(context, environment, stack));
    }

    /// <summary>A path the user typed, relative to the working directory, as a path in the repository.</summary>
    public static string InRepository(GitWorkTree tree, string workingDirectory, string path)
    {
        var relative = Path.GetRelativePath(tree.Root, Path.GetFullPath(path, workingDirectory)).Replace('\\', '/');
        return relative.StartsWith("../", StringComparison.Ordinal) || relative == ".."
            ? throw new CliException($"{path} is outside the repository.", ExitCodes.Usage)
            : relative;
    }
}

/// <summary>
/// <c>forest secrets init</c>: prepares a repository for secrets kept with the forest filter: .sops.yaml with the
/// public keys of the environments (and recovery keys), .gitattributes with the files to encrypt, and the filter.
/// </summary>
internal sealed class SecretsInitCommand() : ForestCommand("init", "Write .sops.yaml and .gitattributes for secrets of environments, and turn the filter on.")
{
    private readonly Option<string[]> environments = new("--env", "-e")
    {
        Description = "Environment whose key encrypts every file not matched by a --rule; repeat for several. " +
            "Defaults to the context's environment or the only one you can see.",
        AllowMultipleArgumentsPerToken = false,
    };

    private readonly Option<string[]> rules = new("--rule")
    {
        Description = "Files under a path prefix are for one environment, or one stack with its own key: --rule stacks/prod/=prod, --rule stacks/prod/web/=prod/web. Repeat for each.",
    };

    private readonly Option<string[]> recovery = new("--recovery")
    {
        Description = "An age public key of your own (age1…) every file is also encrypted for, to recover secrets without the aggregator.",
    };

    private readonly Option<string[]> patterns = new("--pattern")
    {
        Description = $"A .gitattributes pattern of files to encrypt; repeat for several. Default: {string.Join(", ", SecretsFiles.DefaultPatterns)}.",
    };

    private readonly Option<bool> force = new("--force") { Description = "Overwrite an existing .sops.yaml." };

    protected override void Configure(Command command)
    {
        command.Add(environments);
        command.Add(rules);
        command.Add(recovery);
        command.Add(patterns);
        command.Add(force);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        var configPath = Path.Combine(tree.Root, SecretsFiles.SopsConfig);
        if (File.Exists(configPath) && !context.Value(force))
        {
            throw new CliException(
                ".sops.yaml exists already. Use 'forest secrets adopt' for a repository with sops files, or --force to replace it.",
                ExitCodes.Usage);
        }

        var recoveryKeys = (context.Value(recovery) ?? []).ToList();
        foreach (var key in recoveryKeys.Where(k => !SopsFiles.AgeRecipients(Encoding.UTF8.GetBytes(k)).Contains(k)))
        {
            throw new CliException($"'{key}' is not an age public key (age1…).", ExitCodes.Usage);
        }

        var api = context.Api;
        var ct = context.CancellationToken;
        var config = new StringBuilder("""
            # Written by 'forest secrets init' (ADR-0010): the first rule whose path_regex matches a file applies.
            # The aggregator decrypts a file only for Editors of the environments whose keys it is encrypted for.
            creation_rules:

            """);
        foreach (var rule in context.Value(rules) ?? [])
        {
            var (prefix, target) = rule.Split('=', 2) is [var p, var e] && p.Length > 0 && e.Length > 0
                ? (p.TrimStart('/'), e)
                : throw new CliException($"--rule {rule}: expected <path prefix>=<environment> or <environment>/<stack>.", ExitCodes.Usage);
            var (name, recipient) = await SecretsFiles.ResolveKeyAsync(context, target);
            AppendRule(config, SopsConfigFile.PrefixRegex(prefix), [name], [recipient, .. recoveryKeys]);
        }

        var names = context.Value(environments) is { Length: > 0 } named ? named : null;
        var catchAll = names is not null || (context.Value(rules) ?? []).Length == 0
            ? names is null
                ? [await api.ResolveEnvironmentAsync(null, context.Connection.DefaultEnvironment, ct)]
                : await Task.WhenAll(names.Select(n => api.ResolveEnvironmentAsync(n, null, ct)))
            : [];
        if (catchAll.Length > 0)
        {
            var recipients = new List<string>();
            foreach (var environment in catchAll)
            {
                recipients.Add(await RecipientAsync(context, environment));
            }

            AppendRule(config, null, catchAll.Select(e => e.Name!).ToList(), [.. recipients, .. recoveryKeys]);
            if (catchAll.Length > 1)
            {
                context.Output.Note(
                    "Every file is encrypted for all of these environments: an Editor of any one of them can read all of them. " +
                    "Use --rule to keep each environment's secrets apart.");
            }
        }

        await File.WriteAllTextAsync(configPath, config.ToString(), ct);
        var added = SecretsFiles.AddAttributes(
            tree,
            context.Value(patterns) is { Length: > 0 } custom ? custom : SecretsFiles.DefaultPatterns,
            "Secrets the forest git filter keeps encrypted in git (ADR-0010).");
        var refreshed = GitFilterSetup.Apply(tree, context.Connection.ContextName, GitFilterSetup.Executable(), context.Output);

        context.Output.Done($"Wrote .sops.yaml and {added} line(s) of .gitattributes; the forest filter is on ({refreshed.Decrypted} of {refreshed.Total} secret file(s) decrypted).");
        GitSetupCommand.Report(refreshed, context.Output);
        context.Output.Note("Commit .sops.yaml and .gitattributes; files that match them are encrypted when you git add them.");
        if (recoveryKeys.Count == 0)
        {
            context.Output.Note("Without --recovery only the aggregator can decrypt these files: keep its data volume backed up.");
        }

        return ExitCodes.Ok;
    }

    /// <summary>The newest key of the environment, or of one of its stacks (ADR-0012): the one new files are encrypted for.</summary>
    internal static async Task<string> RecipientAsync(CommandContext context, EnvironmentResponse environment, StackResponse? stack = null)
    {
        var keys = await context.Api.ListAgeKeysAsync(environment.Id!.Value, context.CancellationToken);
        return keys.FirstOrDefault(k => k.StackId == stack?.Id)?.Recipient
            ?? throw new CliException(
                stack is null
                    ? $"Environment '{environment.Name}' has no age key; an administrator creates one: forest env key create -e {environment.Name}."
                    : $"Stack '{stack.Name}' has no age key of its own; an administrator of the environment creates one: " +
                      $"forest env key create -e {environment.Name} --stack {stack.Name}.",
                ExitCodes.Failed);
    }

    private static void AppendRule(StringBuilder config, string? pathRegex, IReadOnlyList<string> environmentNames, IReadOnlyList<string> recipients)
    {
        foreach (var line in SopsConfigFile.RuleLines(pathRegex, environmentNames, recipients))
        {
            config.Append(line).Append('\n');
        }
    }
}

/// <summary>
/// <c>forest secrets adopt</c>: for a repository whose secrets are already sops files. Puts each encrypted file under
/// the filter in .gitattributes and turns the filter on; .sops.yaml stays as it is.
/// </summary>
internal sealed class SecretsAdoptCommand() : ForestCommand("adopt", "Put the sops files already in this repository under the forest filter.")
{
    public override bool NeedsConnection => false;

    protected override Task<int> ExecuteAsync(CommandContext context)
    {
        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        var tracked = tree.TrackedFiles();
        var filtered = tree.Filtered(tracked).ToHashSet(StringComparer.Ordinal);
        var encrypted = tracked
            .Where(p => !filtered.Contains(p) && tree.WorkTreeFile(p) is { } content && SopsFiles.IsEncrypted(p, content))
            .ToList();

        var skipped = encrypted.Where(p => p.Any(char.IsWhiteSpace)).ToList();
        foreach (var path in skipped)
        {
            context.Output.Note($"{path}: a name with spaces needs a quoted pattern; add it to .gitattributes yourself.");
        }

        var added = SecretsFiles.AddAttributes(
            tree, encrypted.Except(skipped).Select(p => "/" + p), "SOPS files adopted by 'forest secrets adopt' (ADR-0010).");
        if (!File.Exists(Path.Combine(tree.Root, SecretsFiles.SopsConfig)))
        {
            context.Output.Note("There is no .sops.yaml: changed files cannot be encrypted until there is one ('forest secrets init').");
        }

        var refreshed = GitFilterSetup.Apply(tree, GitSetupCommand.CurrentContextName(context), GitFilterSetup.Executable(), context.Output);
        context.Output.Done($"Adopted {added} sops file(s); the forest filter is on ({refreshed.Decrypted} of {refreshed.Total} secret file(s) decrypted).");
        GitSetupCommand.Report(refreshed, context.Output);
        if (added > 0)
        {
            context.Output.Note(
                "Commit .gitattributes. The aggregator decrypts only files encrypted for an environment's key: check with " +
                "'forest secrets status', add the keys to .sops.yaml and run 'forest secrets updatekeys' where needed.");
        }

        return Task.FromResult(ExitCodes.Ok);
    }
}

/// <summary><c>forest secrets add &lt;file&gt;…</c>: puts files under the filter, so git stores them encrypted from now on.</summary>
internal sealed class SecretsAddCommand() : ForestCommand("add", "Put files under the forest filter: encrypted in git from the next commit.")
{
    private readonly Argument<string[]> files = new("file") { Description = "Files to keep encrypted.", Arity = ArgumentArity.OneOrMore };

    public override bool NeedsConnection => false;

    protected override void Configure(Command command) => command.Add(files);

    protected override Task<int> ExecuteAsync(CommandContext context)
    {
        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        SecretsFiles.EnsureFilterSetUp(tree);
        var paths = context.Value(files).Select(f => SecretsFiles.InRepository(tree, context.Host.WorkingDirectory, f)).ToList();
        foreach (var path in paths.Where(p => p.Any(char.IsWhiteSpace)))
        {
            throw new CliException($"{path}: a name with spaces needs a quoted pattern; add it to .gitattributes yourself.", ExitCodes.Usage);
        }

        var added = SecretsFiles.AddAttributes(tree, paths.Select(p => "/" + p), "Added by 'forest secrets add' (ADR-0010).");
        var tracked = tree.TrackedFiles().ToHashSet(StringComparer.Ordinal);
        var inGit = paths.Where(tracked.Contains).ToList();
        foreach (var path in inGit.Where(p => !SopsFiles.IsEncrypted(p, tree.IndexBlob(p))))
        {
            context.Output.Note($"{path} is already in git in plain text: change that secret once the encrypted file is committed.");
        }

        // Stage the tracked ones through the clean filter now, so the next commit has them encrypted.
        tree.Renormalize(inGit);
        context.Output.Done($"{added} file(s) added to .gitattributes; commit it{(inGit.Count > 0 ? " with the re-encrypted files" : "")}.");
        return Task.FromResult(ExitCodes.Ok);
    }
}

/// <summary>
/// <c>forest secrets remove &lt;file&gt;…</c>: takes files out from under the filter. By default git keeps them as the
/// sops files they are, and the clone gets the ciphertext back (edit them with sops from now on); with
/// <c>--plain</c> the decrypted text is staged for the next commit, for a file that turned out to be no secret.
/// </summary>
internal sealed class SecretsRemoveCommand() : ForestCommand("remove", "Take files out from under the forest filter: kept encrypted in git, or with --plain committed as plain text.")
{
    private readonly Argument<string[]> files = new("file") { Description = "Files under the filter.", Arity = ArgumentArity.OneOrMore };

    private readonly Option<bool> plain = new("--plain") { Description = "Stage the decrypted text: the file is no secret any more (needs --yes)." };

    private readonly Option<bool> yes = Confirmation.Option();

    public override bool NeedsConnection => false;

    protected override void Configure(Command command)
    {
        command.Add(files);
        command.Add(plain);
        command.Add(yes);
    }

    protected override Task<int> ExecuteAsync(CommandContext context)
    {
        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        var paths = context.Value(files).Select(f => SecretsFiles.InRepository(tree, context.Host.WorkingDirectory, f)).Distinct().ToList();
        var notFiltered = paths.Except(tree.Filtered(paths)).ToList();
        if (notFiltered.Count > 0)
        {
            throw new CliException($"Not under filter=forest: {string.Join(", ", notFiltered)}.", ExitCodes.Usage);
        }

        var tracked = tree.TrackedFiles().ToHashSet(StringComparer.Ordinal);
        var asPlain = context.Value(plain);
        if (asPlain)
        {
            Confirmation.Require(context, yes, $"commit {string.Join(", ", paths)} as plain text that anyone who can read the repository sees");
            var encrypted = paths.Where(p => tree.WorkTreeFile(p) is not { } content || SopsFiles.IsEncrypted(p, content)).ToList();
            if (encrypted.Count > 0)
            {
                throw new CliException($"Not decrypted in this clone, so there is no plain text to commit: {string.Join(", ", encrypted)}.", ExitCodes.Failed);
            }
        }
        else
        {
            var untracked = paths.Where(p => !tracked.Contains(p)).ToList();
            if (untracked.Count > 0)
            {
                throw new CliException(
                    $"Not in git yet, so there is no encrypted file to keep: {string.Join(", ", untracked)}. Delete it, or use --plain.", ExitCodes.Usage);
            }

            var plainInGit = paths.Where(p => !SopsFiles.IsEncrypted(p, tree.IndexBlob(p))).ToList();
            if (plainInGit.Count > 0)
            {
                throw new CliException(
                    $"Plain text in git already, so there is no encrypted file to keep: {string.Join(", ", plainInGit)}. Use --plain.", ExitCodes.Usage);
            }

            // Checked while the filter still applies: the plain text is about to be replaced by what git has.
            var changed = paths.Where(p => !tree.IsUnmodified(p)).ToList();
            if (changed.Count > 0)
            {
                throw new CliException(
                    $"Changed and not committed: {string.Join(", ", changed)}. Commit or discard the changes first.", ExitCodes.Failed);
            }
        }

        SecretsFiles.RemoveAttributes(tree, paths, "Taken out from under the filter by 'forest secrets remove' (ADR-0010).");
        var cache = FilterCache.Open(tree.GitDirectory);
        cache.Forget(paths);
        cache.Save();
        var inGit = paths.Where(tracked.Contains).ToList();
        if (asPlain)
        {
            tree.Stage(paths);
            context.Output.Done($"{paths.Count} file(s) staged as plain text; commit them with .gitattributes.");
            if (inGit.Count > 0)
            {
                context.Output.Note("The encrypted versions stay in the history; anyone with an environment key of theirs can still read them.");
            }
        }
        else
        {
            _ = tree.CheckOutAgain(inGit);
            context.Output.Done($"{paths.Count} file(s) are sops files outside the filter now; commit .gitattributes.");
            context.Output.Note("They are encrypted in this clone too: edit them with sops, which needs an age key of your own among their recipients.");
        }

        return Task.FromResult(ExitCodes.Ok);
    }
}

/// <summary>
/// <c>forest secrets check</c>: fails when a file under the filter is in plain text in the index, i.e. would be
/// committed or was committed without the filter. The pre-commit hook runs it with <c>--staged</c>; CI without it.
/// </summary>
internal sealed class SecretsCheckCommand() : ForestCommand("check", "Fail if a file under filter=forest is not encrypted in git (for hooks and CI).")
{
    private readonly Option<bool> staged = new("--staged") { Description = "Only the files staged for the next commit." };

    public override bool NeedsConnection => false;

    protected override void Configure(Command command) => command.Add(staged);

    protected override Task<int> ExecuteAsync(CommandContext context)
    {
        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        var filtered = tree.Filtered(context.Value(staged) ? tree.StagedFiles() : tree.TrackedFiles());
        var blobs = filtered.ToDictionary(p => p, tree.IndexBlob, StringComparer.Ordinal);
        var plain = filtered.Where(p => !SopsFiles.IsEncrypted(p, blobs[p])).ToList();
        var crlf = filtered.Except(plain).Where(p => SopsFiles.HasCrLf(blobs[p])).ToList();
        SecretsFiles.NoteLineEndings(tree, filtered, context.Output);
        if (plain.Count == 0 && crlf.Count == 0)
        {
            context.Output.Done($"{filtered.Count} secret file(s) checked: all encrypted.");
            return Task.FromResult(ExitCodes.Ok);
        }

        foreach (var path in plain)
        {
            context.Output.Note($"forest: {path} is under filter=forest but not encrypted in git.");
        }

        foreach (var path in crlf)
        {
            context.Output.Note($"forest: {path} is encrypted in git with CRLF line endings, which sops cannot read.");
        }

        var problems = new List<string>();
        if (plain.Count > 0)
        {
            problems.Add(
                $"{plain.Count} secret file(s) in plain text. Turn the filter on ('forest git setup'), then 'git add --renormalize' them; " +
                "a secret that was ever committed in plain text must be changed.");
        }

        if (crlf.Count > 0)
        {
            problems.Add($"{crlf.Count} secret file(s) with CRLF line endings. Run 'forest secrets fix-eol' and commit.");
        }

        throw new CliException(string.Join(' ', problems));
    }
}

/// <summary>
/// <c>forest secrets updatekeys</c>: encrypts the decrypted files again for the current .sops.yaml, e.g. after adding
/// an environment's new key, like <c>sops updatekeys</c> but through the filter.
/// </summary>
internal sealed class SecretsUpdateKeysCommand() : ForestCommand("updatekeys", "Encrypt the secret files again for the current .sops.yaml.")
{
    private readonly Argument<string[]> files = new("file") { Description = "Files to re-encrypt; all secret files by default.", Arity = ArgumentArity.ZeroOrMore };

    public override bool NeedsConnection => false;

    protected override void Configure(Command command) => command.Add(files);

    protected override Task<int> ExecuteAsync(CommandContext context)
    {
        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        SecretsFiles.EnsureFilterSetUp(tree);
        var requested = context.Value(files) is { Length: > 0 } given
            ? given.Select(f => SecretsFiles.InRepository(tree, context.Host.WorkingDirectory, f)).ToList()
            : tree.TrackedFiles().ToList();
        var filtered = tree.Filtered(requested);
        var decrypted = filtered.Where(p => tree.WorkTreeFile(p) is { } content && !SopsFiles.IsEncrypted(p, content)).ToList();
        foreach (var path in filtered.Except(decrypted))
        {
            context.Output.Note($"{path} stays as it is: it is not decrypted in this clone.");
        }

        // Forgotten first: the filter git starts for 'add' reads the cache from disk.
        var cache = FilterCache.Open(tree.GitDirectory);
        cache.Forget(decrypted);
        cache.Save();
        tree.Renormalize(decrypted);
        context.Output.Done($"Re-encrypted {decrypted.Count} file(s) for the current .sops.yaml; commit them.");
        return Task.FromResult(ExitCodes.Ok);
    }
}

/// <summary>
/// <c>forest secrets fix-eol</c>: keeps the secret files in LF, which sops needs to read dotenv and INI files, on every
/// machine. Gives eol=lf to the forest lines of the root .gitattributes (git otherwise converts the ciphertext to CRLF
/// before smudge where core.autocrlf is on, the default of Git for Windows), stages without CR the files git has with
/// CRLF, and checks out again the ciphertext this clone has with CRLF.
/// </summary>
internal sealed class SecretsFixEolCommand() : ForestCommand("fix-eol", "Keep the secret files in LF: eol=lf in .gitattributes, CRLF out of git and this clone.")
{
    public override bool NeedsConnection => false;

    protected override Task<int> ExecuteAsync(CommandContext context)
    {
        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        var attributes = Path.Combine(tree.Root, SecretsFiles.Attributes);
        var lines = 0;
        if (File.Exists(attributes))
        {
            (var text, lines) = SecretsFiles.WithLineEndings(File.ReadAllText(attributes));
            if (lines > 0)
            {
                File.WriteAllText(attributes, text);
            }
        }

        var filtered = tree.Filtered(tree.TrackedFiles());
        var elsewhere = SecretsFiles.WithoutLineEndings(tree, filtered);
        foreach (var path in elsewhere)
        {
            context.Output.Note($"{path}: another .gitattributes puts it under the filter without eol=lf; add eol=lf there yourself.");
        }

        // The content itself, not 'git add': that would also stage uncommitted changes to the plain text.
        var staged = 0;
        foreach (var path in filtered)
        {
            var blob = tree.IndexBlob(path);
            if (SopsFiles.IsEncrypted(path, blob) && SopsFiles.HasCrLf(blob))
            {
                tree.StageContent(path, SopsFiles.WithoutCr(blob));
                staged++;
            }
        }

        var refreshed = GitFilterSetup.Refresh(tree);
        context.Output.Done(
            $"{lines} line(s) of .gitattributes now keep LF, {staged} file(s) staged without CRLF; " +
            $"{refreshed.Decrypted} of {refreshed.Total} secret file(s) decrypted here.");
        GitSetupCommand.Report(refreshed, context.Output);
        if (lines + staged > 0)
        {
            context.Output.Note("Commit .gitattributes and the staged files, so every clone gets them in LF.");
        }

        return Task.FromResult(ExitCodes.Ok);
    }
}

/// <summary>
/// <c>forest secrets status</c>: each file under the filter, whether it is decrypted here and encrypted in git, and the
/// environments whose keys can decrypt it (of those you can see).
/// </summary>
internal sealed class SecretsStatusCommand() : ForestCommand("status", "Show the secret files: decrypted here, encrypted in git, for which environments.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        var environmentsByRecipient = await SecretsFiles.EnvironmentsByRecipientAsync(context);

        var files = new List<SecretFileStatus>();
        foreach (var path in tree.Filtered(tree.TrackedFiles()))
        {
            var inGit = tree.IndexBlob(path);
            var encryptedInGit = SopsFiles.IsEncrypted(path, inGit);
            var recipients = encryptedInGit ? SopsFiles.AgeRecipients(inGit) : new HashSet<string>();
            var known = recipients.Where(environmentsByRecipient.ContainsKey).Select(r => environmentsByRecipient[r]).Distinct().Order().ToList();
            var workTree = tree.WorkTreeFile(path) switch
            {
                null => "missing",
                var content when SopsFiles.IsEncrypted(path, content) => "encrypted",
                _ => "decrypted",
            };
            files.Add(new SecretFileStatus(path, workTree, encryptedInGit, known, recipients.Count - recipients.Count(environmentsByRecipient.ContainsKey)));
        }

        if (context.Output.IsJson)
        {
            await context.Output.Out.WriteLineAsync(JsonSerializer.Serialize(files, SecretsJsonContext.Default.ListSecretFileStatus));
            return ExitCodes.Ok;
        }

        if (files.Count == 0)
        {
            context.Output.Note("No files are under filter=forest; see 'forest secrets init' or 'forest secrets add'.");
            return ExitCodes.Ok;
        }

        var table = new Table("FILE", "HERE", "IN GIT", "ENVIRONMENTS", "OTHER KEYS");
        foreach (var file in files)
        {
            table.AddRow(file.Path, file.WorkTree, file.EncryptedInGit ? "encrypted" : "PLAIN TEXT", string.Join(", ", file.Environments), file.OtherRecipients.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        table.Render(context.Output.Out);
        foreach (var file in files.Where(f => f.Environments.Count > 1))
        {
            context.Output.Note($"{file.Path} is encrypted for several environments: an Editor of any of them can read it.");
        }

        foreach (var file in files.Where(f => f.EncryptedInGit && f.Environments.Count == 0))
        {
            context.Output.Note($"{file.Path} is for no environment you can see: the aggregator will not decrypt it for you or deploy it.");
        }

        foreach (var file in files.Where(f => f.EncryptedInGit && SopsFiles.HasCrLf(tree.IndexBlob(f.Path))))
        {
            context.Output.Note($"{file.Path} is encrypted in git with CRLF line endings, which sops cannot read: run 'forest secrets fix-eol'.");
        }

        foreach (var file in files.Where(f => f.WorkTree == "encrypted" && SopsFiles.HasCrLf(tree.WorkTreeFile(f.Path)!)))
        {
            context.Output.Note($"{file.Path} has CRLF line endings in this clone, which sops cannot read: run 'forest secrets fix-eol'.");
        }

        SecretsFiles.NoteLineEndings(tree, files.Select(f => f.Path).ToList(), context.Output);
        return files.Any(f => !f.EncryptedInGit) ? ExitCodes.Failed : ExitCodes.Ok;
    }
}

/// <param name="WorkTree">decrypted, encrypted (no access here) or missing.</param>
/// <param name="Environments">Environments you can see whose keys are recipients.</param>
/// <param name="OtherRecipients">Recipients that are no key of a visible environment: personal or recovery keys.</param>
internal sealed record SecretFileStatus(string Path, string WorkTree, bool EncryptedInGit, IReadOnlyList<string> Environments, int OtherRecipients);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<SecretFileStatus>))]
internal sealed partial class SecretsJsonContext : JsonSerializerContext;
