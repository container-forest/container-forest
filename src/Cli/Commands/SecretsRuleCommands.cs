using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContainerForest.Cli.Output;
using ContainerForest.Cli.Secrets;

namespace ContainerForest.Cli.Commands;

/// <summary>Reads and writes the .sops.yaml of the repository the command runs in.</summary>
internal static class SopsConfigEditing
{
    public static string PathIn(GitWorkTree tree) => Path.Combine(tree.Root, SecretsFiles.SopsConfig);

    /// <exception cref="CliException">There is no .sops.yaml, or it is not in a form the commands can edit.</exception>
    public static SopsConfigFile Load(GitWorkTree tree)
    {
        var path = PathIn(tree);
        return File.Exists(path)
            ? SopsConfigFile.Parse(File.ReadAllText(path))
            : throw new CliException("There is no .sops.yaml here: run 'forest secrets init' first.", ExitCodes.Usage);
    }

    public static string Describe(SopsConfigFile.Rule rule) =>
        rule.PathRegex is null ? "(every other file)" : SopsConfigFile.Rule.AsPrefix(rule.PathRegex) ?? $"/{rule.PathRegex}/";

    public static void NoteUpdateKeys(CliOutput output) =>
        output.Note("Commit .sops.yaml, then run 'forest secrets updatekeys' so the files already in git are encrypted for the new rules.");
}

/// <summary>
/// <c>forest secrets rule list</c>: the creation rules of .sops.yaml in the order sops tries them, with the
/// environments their keys belong to (of those you can see).
/// </summary>
internal sealed class SecretsRuleListCommand() : ForestCommand("list", "List the rules of .sops.yaml and the environments whose keys they encrypt for.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var config = SopsConfigEditing.Load(GitWorkTree.Open(context.Host.WorkingDirectory));
        var environmentsByRecipient = await SecretsFiles.EnvironmentsByRecipientAsync(context);
        var rules = config.Rules.Select(r => new SopsRuleStatus(
                SopsConfigEditing.Describe(r),
                r.PathRegex,
                r.AgeRecipients.Where(environmentsByRecipient.ContainsKey).Select(k => environmentsByRecipient[k]).Distinct().Order().ToList(),
                r.AgeRecipients.Count(k => !environmentsByRecipient.ContainsKey(k)),
                r.OtherKeyTypes))
            .ToList();

        if (context.Output.IsJson)
        {
            await context.Output.Out.WriteLineAsync(JsonSerializer.Serialize(rules, SecretsRuleJsonContext.Default.ListSopsRuleStatus));
            return ExitCodes.Ok;
        }

        var table = new Table("FILES", "ENVIRONMENTS", "OTHER KEYS");
        foreach (var rule in rules)
        {
            var other = rule.OtherAgeKeys.ToString(System.Globalization.CultureInfo.InvariantCulture);
            table.AddRow(rule.Files, string.Join(", ", rule.Environments), rule.OtherKeyTypes.Count > 0 ? $"{other} + {string.Join(", ", rule.OtherKeyTypes)}" : other);
        }

        table.Render(context.Output.Out);
        foreach (var rule in rules.Where(r => r.OtherKeyTypes.Count > 0))
        {
            context.Output.Note($"{rule.Files}: the aggregator encrypts only with age keys; this rule works with local sops only.");
        }

        return ExitCodes.Ok;
    }
}

/// <summary>
/// <c>forest secrets rule add &lt;prefix&gt;=&lt;environment&gt;</c>: files under a path are encrypted for an environment.
/// The rule goes before any rule it overlaps (sops takes the first match), and keeps the keys every rule has that are
/// no environment's: the recovery keys of 'forest secrets init --recovery'.
/// </summary>
internal sealed class SecretsRuleAddCommand() : ForestCommand("add", "Add a rule to .sops.yaml: files under a path are for one environment.")
{
    private readonly Argument<string> rule = new("rule") { Description = "<path prefix>=<environment>, e.g. stacks/staging/=staging." };

    private readonly Option<string[]> recovery = new("--recovery")
    {
        Description = "Another age public key (age1…) of your own the files are also encrypted for.",
    };

    protected override void Configure(Command command)
    {
        command.Add(rule);
        command.Add(recovery);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var (prefix, environmentName) = context.Value(rule).Split('=', 2) is [var p, var e] && p.Trim('/').Length > 0 && e.Length > 0
            ? (p.TrimStart('/'), e)
            : throw new CliException($"{context.Value(rule)}: expected <path prefix>=<environment>.", ExitCodes.Usage);
        var extra = (context.Value(recovery) ?? []).ToList();
        foreach (var key in extra.Where(k => !SopsFiles.AgeRecipients(System.Text.Encoding.UTF8.GetBytes(k)).Contains(k)))
        {
            throw new CliException($"'{key}' is not an age public key (age1…).", ExitCodes.Usage);
        }

        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        var config = SopsConfigEditing.Load(tree);
        var environment = await context.Api.ResolveEnvironmentAsync(environmentName, null, context.CancellationToken);
        var recipient = await SecretsInitCommand.RecipientAsync(context, environment);
        var environmentKeys = await SecretsFiles.EnvironmentsByRecipientAsync(context);
        var kept = config.Rules.Count == 0
            ? []
            : config.Rules.Select(r => r.AgeRecipients.AsEnumerable())
                .Aggregate((all, next) => all.Intersect(next, StringComparer.Ordinal))
                .Where(k => !environmentKeys.ContainsKey(k))
                .ToList();

        var position = config.AddPrefixRule(prefix, [environment.Name!], [recipient, .. kept, .. extra]);
        await File.WriteAllTextAsync(SopsConfigEditing.PathIn(tree), config.ToString(), context.CancellationToken);

        var before = position + 1 < config.Rules.Count ? $", before {SopsConfigEditing.Describe(config.Rules[position + 1])}" : "";
        context.Output.Done($"Files under {prefix} are for environment '{environment.Name}' now (rule {position + 1} of {config.Rules.Count}{before}).");
        if (kept.Count > 0)
        {
            context.Output.Note($"The rule keeps {kept.Count} key(s) every other rule has too (recovery keys).");
        }

        SopsConfigEditing.NoteUpdateKeys(context.Output);
        return ExitCodes.Ok;
    }
}

/// <summary><c>forest secrets rule remove &lt;prefix&gt;</c>: drops the rule for a path; its files fall to the next rule that matches.</summary>
internal sealed class SecretsRuleRemoveCommand() : ForestCommand("remove", "Remove the rule for a path prefix from .sops.yaml.")
{
    private readonly Argument<string> prefix = new("prefix") { Description = "The path prefix of the rule, as 'rule list' shows it." };

    public override bool NeedsConnection => false;

    protected override void Configure(Command command) => command.Add(prefix);

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var tree = GitWorkTree.Open(context.Host.WorkingDirectory);
        var config = SopsConfigEditing.Load(tree);
        config.RemovePrefixRule(context.Value(prefix));
        await File.WriteAllTextAsync(SopsConfigEditing.PathIn(tree), config.ToString(), context.CancellationToken);

        context.Output.Done($"Removed the rule for {context.Value(prefix).TrimStart('/')}.");
        context.Output.Note(config.Rules.Count == 0
            ? "No rules are left: files under the filter cannot be encrypted until there is one."
            : "Its files fall to the next rule that matches them.");
        SopsConfigEditing.NoteUpdateKeys(context.Output);
        return ExitCodes.Ok;
    }
}

/// <param name="Files">The path prefix, "(every other file)" or the regex.</param>
/// <param name="OtherAgeKeys">Age keys that are no key of a visible environment: personal or recovery keys.</param>
/// <param name="OtherKeyTypes">pgp, kms and the like, which only local sops uses.</param>
internal sealed record SopsRuleStatus(string Files, string? PathRegex, IReadOnlyList<string> Environments, int OtherAgeKeys, IReadOnlyList<string> OtherKeyTypes);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<SopsRuleStatus>))]
internal sealed partial class SecretsRuleJsonContext : JsonSerializerContext;
