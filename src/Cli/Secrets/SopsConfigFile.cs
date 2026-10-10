using System.Text;
using System.Text.RegularExpressions;

namespace ContainerForest.Cli.Secrets;

/// <summary>
/// The creation rules of a .sops.yaml, edited as lines: the CLI has no YAML library (NativeAOT), and a rule it does not
/// change keeps its text, comments included. It understands the block list 'forest secrets init' writes and the usual
/// hand-written form of it; anything else is refused rather than rewritten.
/// </summary>
internal sealed partial class SopsConfigFile
{
    private readonly List<string> header;
    private readonly List<Rule> rules;
    private readonly List<string> trailer;

    private SopsConfigFile(List<string> header, List<Rule> rules, List<string> trailer)
    {
        this.header = header;
        this.rules = rules;
        this.trailer = trailer;
    }

    /// <summary>The rules in file order; sops takes the first whose path_regex matches.</summary>
    public IReadOnlyList<Rule> Rules => rules;

    /// <exception cref="CliException">The file is not in a form these commands can edit.</exception>
    public static SopsConfigFile Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var start = lines.FindIndex(l => CreationRules().IsMatch(l));
        if (start < 0)
        {
            throw Unsupported("there is no 'creation_rules:' list");
        }

        var header = lines[..(start + 1)];
        var rules = new List<Rule>();
        var pending = new List<string>();
        string? indent = null;
        var end = lines.Count;
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                // A comment or blank line belongs to the rule after it ('# environment prod' that init writes).
                pending.Add(line);
                continue;
            }

            if (!char.IsWhiteSpace(line[0]) && !line.StartsWith("- ", StringComparison.Ordinal))
            {
                end = i;
                break;
            }

            var item = ListItem().Match(line);
            if (item.Success && (indent is null || item.Groups["indent"].Value == indent))
            {
                indent = item.Groups["indent"].Value;
                rules.Add(new Rule([.. pending, line]));
                pending.Clear();
            }
            else if (rules.Count > 0 && line.Length > indent!.Length + 2 && line[..(indent.Length + 2)].Trim().Length == 0)
            {
                rules[^1].Lines.AddRange(pending);
                rules[^1].Lines.Add(line);
                pending.Clear();
            }
            else
            {
                throw Unsupported($"line {i + 1} is not part of a creation rule this command understands");
            }
        }

        foreach (var rule in rules)
        {
            rule.Read(indent!);
        }

        return new SopsConfigFile(header, rules, [.. pending, .. lines[end..]]);
    }

    /// <summary>
    /// A rule for files under a path prefix, written the way 'forest secrets init' writes it, inserted before the first
    /// rule that also matches files under the prefix or files of which would match it: otherwise one would shadow the
    /// other.
    /// </summary>
    /// <returns>The position it took.</returns>
    /// <exception cref="CliException">There is a rule for this prefix already.</exception>
    public int AddPrefixRule(string prefix, IReadOnlyList<string> environmentNames, IReadOnlyList<string> recipients)
    {
        var pathRegex = PrefixRegex(prefix);
        if (rules.Any(r => r.PathRegex == pathRegex))
        {
            throw new CliException($"There is a rule for {prefix} already; remove it first.", ExitCodes.Usage);
        }

        var position = rules.FindIndex(r => r.Overlaps(prefix));
        position = position < 0 ? rules.Count : position;
        var rule = new Rule([.. RuleLines(pathRegex, environmentNames, recipients, rules.FirstOrDefault()?.Indent ?? "  ")]);
        rule.Read(rules.FirstOrDefault()?.Indent ?? "  ");
        rules.Insert(position, rule);
        return position;
    }

    /// <exception cref="CliException">No rule is for this prefix.</exception>
    public Rule RemovePrefixRule(string prefix)
    {
        var pathRegex = PrefixRegex(prefix);
        var rule = rules.Find(r => r.PathRegex == pathRegex)
            ?? throw new CliException($"No rule in .sops.yaml is for {prefix} (path_regex '{pathRegex}').", ExitCodes.Usage);
        rules.Remove(rule);
        return rule;
    }

    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (var line in header.Concat(rules.SelectMany(r => r.Lines)).Concat(trailer))
        {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The lines of a rule as 'forest secrets init' writes them: a comment naming the environments, then the rule.</summary>
    public static IEnumerable<string> RuleLines(string? pathRegex, IReadOnlyList<string> environmentNames, IReadOnlyList<string> recipients, string indent = "  ")
    {
        yield return $"{indent}# {(environmentNames.Count == 1 ? "environment" : "environments")} {string.Join(", ", environmentNames)}";
        var age = $"age: {string.Join(',', recipients.Distinct())}";
        if (pathRegex is null)
        {
            yield return $"{indent}- {age}";
        }
        else
        {
            yield return $"{indent}- path_regex: '{pathRegex.Replace("'", "''", StringComparison.Ordinal)}'";
            yield return $"{indent}  {age}";
        }
    }

    /// <summary>The path_regex of a prefix rule: anchored, with only the metacharacters escaped.</summary>
    public static string PrefixRegex(string prefix)
    {
        // RE2, which sops uses, accepts a backslash only before punctuation; escape just the metacharacters.
        var escaped = new StringBuilder("^");
        foreach (var c in prefix.TrimStart('/'))
        {
            if (@"\.+*?()|[]{}^$".Contains(c, StringComparison.Ordinal))
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    private static CliException Unsupported(string reason) =>
        new($".sops.yaml: {reason}; edit it yourself.", ExitCodes.Failed);

    [GeneratedRegex(@"^creation_rules:\s*(#.*)?$")]
    private static partial Regex CreationRules();

    [GeneratedRegex(@"^(?<indent>[ ]*)- \S")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*(-\s+)?(?<key>[a-z_]+):\s*(?<value>.*?)\s*$")]
    private static partial Regex KeyValue();

    /// <summary>One creation rule: its lines as they are in the file, and what the commands need to know of it.</summary>
    internal sealed class Rule(List<string> lines)
    {
        public List<string> Lines { get; } = lines;

        /// <summary>null: the rule matches every file.</summary>
        public string? PathRegex { get; private set; }

        public IReadOnlyList<string> AgeRecipients { get; private set; } = [];

        /// <summary>Key types other than age (pgp, kms, …): the aggregator refuses to encrypt with such a rule.</summary>
        public IReadOnlyList<string> OtherKeyTypes { get; private set; } = [];

        public string Indent { get; private set; } = "  ";

        /// <summary>
        /// Whether this rule could take files under the prefix, or files under the prefix rule could take some of its
        /// files: then their order decides, and the narrower one has to come first.
        /// </summary>
        public bool Overlaps(string prefix)
        {
            if (PathRegex is null)
            {
                return true;
            }

            var other = AsPrefix(PathRegex);
            if (other is not null)
            {
                return prefix.StartsWith(other, StringComparison.Ordinal) || other.StartsWith(prefix, StringComparison.Ordinal);
            }

            try
            {
                return Regex.IsMatch(prefix + "file", PathRegex, RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        public void Read(string indent)
        {
            Indent = indent;
            var keys = new List<string>();
            foreach (var line in Lines.Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')))
            {
                var match = KeyValue().Match(line);
                if (!match.Success)
                {
                    throw Unsupported($"'{line.Trim()}' is not a key this command understands");
                }

                var (key, value) = (match.Groups["key"].Value, Unquote(WithoutComment(match.Groups["value"].Value)));
                keys.Add(key);
                switch (key)
                {
                    case "path_regex":
                        PathRegex = value;
                        break;
                    case "age":
                        AgeRecipients = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        break;
                }
            }

            OtherKeyTypes = keys.Where(k => k is "pgp" or "kms" or "gcp_kms" or "azure_keyvault" or "hc_vault_transit_uri" or "key_groups").ToList();
        }

        /// <summary>The prefix of a path_regex 'forest secrets init' or 'rule add' wrote; null for any other regex.</summary>
        public static string? AsPrefix(string pathRegex)
        {
            if (!pathRegex.StartsWith('^'))
            {
                return null;
            }

            var prefix = new StringBuilder();
            for (var i = 1; i < pathRegex.Length; i++)
            {
                var c = pathRegex[i];
                if (c == '\\' && i + 1 < pathRegex.Length && @"\.+*?()|[]{}^$".Contains(pathRegex[i + 1], StringComparison.Ordinal))
                {
                    prefix.Append(pathRegex[++i]);
                }
                else if (@"\.+*?()|[]{}^$".Contains(c, StringComparison.Ordinal))
                {
                    return null;
                }
                else
                {
                    prefix.Append(c);
                }
            }

            return prefix.ToString();
        }

        // A '#' after a space starts a comment, except inside a quoted value.
        private static string WithoutComment(string value)
        {
            var at = value.IndexOf(" #", StringComparison.Ordinal);
            return value.StartsWith('\'') || value.StartsWith('"') || at < 0 ? value : value[..at].TrimEnd();
        }

        private static string Unquote(string value) => value switch
        {
            ['\'', .. var inner, '\''] => inner.Replace("''", "'", StringComparison.Ordinal),
            ['"', .. var inner, '"'] => inner.Replace("\\\\", "\\", StringComparison.Ordinal),
            _ => value,
        };
    }
}
