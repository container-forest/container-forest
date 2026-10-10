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
    public const string FilterAttributes = "filter=forest diff=forest";

    /// <summary>Default patterns: dotenv files and anything in a secrets directory, at any depth.</summary>
    public static readonly string[] DefaultPatterns = [".env", "**/secrets/**"];

    /// <summary>Appends the lines .gitattributes does not have yet; returns how many were added.</summary>
    public static int AddAttributes(GitWorkTree tree, IEnumerable<string> patterns, string comment)
    {
        var path = Path.Combine(tree.Root, Attributes);
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var present = existing.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        var lines = patterns.Select(p => $"{p} {FilterAttributes}").Where(l => !present.Contains(l)).ToList();
        if (lines.Count == 0)
        {
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
        Description = "Files under a path prefix are for one environment: --rule stacks/prod/=prod. Repeat for each.",
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
            var (prefix, environmentName) = rule.Split('=', 2) is [var p, var e] && p.Length > 0 && e.Length > 0
                ? (p.TrimStart('/'), e)
                : throw new CliException($"--rule {rule}: expected <path prefix>=<environment>.", ExitCodes.Usage);
            var environment = await api.ResolveEnvironmentAsync(environmentName, null, ct);
            AppendRule(config, $"^{EscapeRegex(prefix)}", [environment.Name!], [await RecipientAsync(context, environment), .. recoveryKeys]);
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
        var (decrypted, total) = GitFilterSetup.Apply(tree, context.Connection.ContextName, GitFilterSetup.Executable(), context.Output);

        context.Output.Done($"Wrote .sops.yaml and {added} line(s) of .gitattributes; the forest filter is on ({decrypted} of {total} secret file(s) decrypted).");
        context.Output.Note("Commit .sops.yaml and .gitattributes; files that match them are encrypted when you git add them.");
        if (recoveryKeys.Count == 0)
        {
            context.Output.Note("Without --recovery only the aggregator can decrypt these files: keep its data volume backed up.");
        }

        return ExitCodes.Ok;
    }

    private static async Task<string> RecipientAsync(CommandContext context, EnvironmentResponse environment)
    {
        var keys = await context.Api.ListAgeKeysAsync(environment.Id!.Value, context.CancellationToken);
        return keys.FirstOrDefault()?.Recipient
            ?? throw new CliException(
                $"Environment '{environment.Name}' has no age key; an administrator creates one: forest env key create -e {environment.Name}.",
                ExitCodes.Failed);
    }

    private static void AppendRule(StringBuilder config, string? pathRegex, IReadOnlyList<string> environmentNames, IReadOnlyList<string> recipients)
    {
        config.Append("  # ").Append(environmentNames.Count == 1 ? "environment " : "environments ").AppendJoin(", ", environmentNames).Append('\n');
        config.Append(pathRegex is null ? "  - age: " : $"  - path_regex: '{pathRegex.Replace("'", "''", StringComparison.Ordinal)}'\n    age: ")
            .AppendJoin(',', recipients.Distinct()).Append('\n');
    }

    // RE2, which sops uses, accepts a backslash only before punctuation; escape just the metacharacters.
    private static string EscapeRegex(string text)
    {
        var escaped = new StringBuilder();
        foreach (var c in text)
        {
            if (@"\.+*?()|[]{}^$".Contains(c, StringComparison.Ordinal))
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
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

        var (decrypted, total) = GitFilterSetup.Apply(tree, GitSetupCommand.CurrentContextName(context), GitFilterSetup.Executable(), context.Output);
        context.Output.Done($"Adopted {added} sops file(s); the forest filter is on ({decrypted} of {total} secret file(s) decrypted).");
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
        var plain = filtered.Where(p => !SopsFiles.IsEncrypted(p, tree.IndexBlob(p))).ToList();
        if (plain.Count == 0)
        {
            context.Output.Done($"{filtered.Count} secret file(s) checked: all encrypted.");
            return Task.FromResult(ExitCodes.Ok);
        }

        foreach (var path in plain)
        {
            context.Output.Note($"forest: {path} is under filter=forest but not encrypted in git.");
        }

        throw new CliException(
            $"{plain.Count} secret file(s) in plain text. Turn the filter on ('forest git setup'), then 'git add --renormalize' them; " +
            "a secret that was ever committed in plain text must be changed.");
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
/// <c>forest secrets status</c>: each file under the filter, whether it is decrypted here and encrypted in git, and the
/// environments whose keys can decrypt it (of those you can see).
/// </summary>
internal sealed class SecretsStatusCommand() : ForestCommand("status", "Show the secret files: decrypted here, encrypted in git, for which environments.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        var environmentsByRecipient = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var environment in await context.Api.ListEnvironmentsAsync(context.CancellationToken))
        {
            foreach (var key in await context.Api.ListAgeKeysAsync(environment.Id!.Value, context.CancellationToken))
            {
                environmentsByRecipient[key.Recipient!] = environment.Name!;
            }
        }

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
