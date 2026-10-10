using System.Text;
using System.Text.RegularExpressions;

namespace ContainerForest.Aggregator.Application.Secrets;

/// <summary>A <c>.gitattributes</c> file of the repository.</summary>
/// <param name="Directory">Its directory relative to the repository root, '/'-separated; empty for the root.</param>
public sealed record GitAttributesFile(string Directory, string Content);

/// <summary>
/// Which files <c>.gitattributes</c> puts under the forest git filter (<c>filter=forest</c>, ADR-0010). Follows git's
/// rules for the one attribute: patterns as in .gitignore relative to the file's directory, a deeper file overrides a
/// shallower one, a later line overrides an earlier one; <c>-filter</c>, <c>!filter</c> and another driver unset it.
/// Macros and quoted patterns are not supported: such lines are skipped.
/// </summary>
public sealed class GitAttributes
{
    public const string FileName = ".gitattributes";

    public const string Driver = "forest";

    private readonly List<Rule> rules = [];

    /// <param name="files">In any order; they are applied from the root down.</param>
    public GitAttributes(IEnumerable<GitAttributesFile> files)
    {
        foreach (var file in files.OrderBy(f => f.Directory.Length == 0 ? 0 : f.Directory.Count(c => c == '/') + 1))
        {
            foreach (var line in file.Content.Split('\n'))
            {
                if (Parse(file.Directory, line) is { } rule)
                {
                    rules.Add(rule);
                }
            }
        }
    }

    /// <param name="path">Relative to the repository root, '/'-separated.</param>
    public bool IsFiltered(string path)
    {
        var filtered = false;
        foreach (var rule in rules)
        {
            if (rule.Matches(path))
            {
                filtered = rule.SetsForest;
            }
        }

        return filtered;
    }

    private static Rule? Parse(string directory, string line)
    {
        var fields = line.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || fields[0].StartsWith('#') || fields[0].StartsWith('"') || fields[0].StartsWith("[attr]", StringComparison.Ordinal))
        {
            return null;
        }

        bool? setsForest = null;
        foreach (var attribute in fields.Skip(1))
        {
            if (attribute.StartsWith("filter=", StringComparison.Ordinal))
            {
                setsForest = attribute["filter=".Length..] == Driver;
            }
            else if (attribute is "-filter" or "!filter" or "filter")
            {
                setsForest = false;
            }
        }

        return setsForest is { } value ? new Rule(PatternRegex(directory, fields[0]), value) : null;
    }

    // .gitignore patterns: one without a slash (other than a trailing one) matches a name at any depth below the
    // directory; one with a slash is anchored to the directory.
    private static Regex PatternRegex(string directory, string pattern)
    {
        var anchored = pattern.TrimEnd('/').Contains('/');
        pattern = pattern.TrimStart('/');
        var prefix = directory.Length == 0 ? "" : Regex.Escape(directory) + "/";
        var body = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var slashAfter = i + 2 < pattern.Length && pattern[i + 2] == '/';
                body.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*')
            {
                body.Append("[^/]*");
            }
            else if (c == '?')
            {
                body.Append("[^/]");
            }
            else if (c == '[' && pattern.IndexOf(']', i + 1) is var end and > 0)
            {
                var set = pattern[(i + 1)..end];
                body.Append('[').Append(set.StartsWith('!') ? "^" + Regex.Escape(set[1..]) : Regex.Escape(set)).Append(']');
                i = end;
            }
            else
            {
                body.Append(Regex.Escape(c.ToString()));
            }
        }

        var anywhere = anchored ? "" : "(?:.*/)?";
        return new Regex($@"\A{prefix}{anywhere}{body}\z", RegexOptions.CultureInvariant);
    }

    private sealed record Rule(Regex Pattern, bool SetsForest)
    {
        public bool Matches(string path) => Pattern.IsMatch(path);
    }
}
