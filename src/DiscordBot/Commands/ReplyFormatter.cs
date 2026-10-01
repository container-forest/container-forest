using System.Text;
using ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Commands;

/// <summary>Turns the aggregator's answers into Discord messages within Discord's limits.</summary>
public static class ReplyFormatter
{
    /// <summary>Discord rejects longer message content.</summary>
    public const int MaxMessageLength = 2000;

    public static string Environments(IReadOnlyList<ForestEnvironment> environments)
    {
        if (environments.Count == 0)
        {
            return "You cannot see any environments yet. Ask an administrator for access.";
        }

        var lines = environments
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => $"{(e.Online ? "🟢" : "🔴")} **{Markdown.Escape(e.Name)}** — {(e.Online ? "online" : "agent offline")}")
            .ToList();
        return WithinLimit("**Environments**", lines, "environments");
    }

    /// <summary>Containers grouped by compose stack; containers outside compose come last.</summary>
    public static string Containers(ForestEnvironment environment, IReadOnlyList<ForestContainer> containers)
    {
        var header = $"**{Markdown.Escape(environment.Name)}** · {Count(containers.Count, "container")}";
        if (containers.Count == 0)
        {
            return header;
        }

        var lines = new List<string>();
        var stacks = containers
            .GroupBy(c => c.StackName)
            .OrderBy(g => g.Key is null)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var stack in stacks)
        {
            lines.Add(stack.Key is null ? "__Outside stacks__" : $"__Stack {Markdown.Escape(stack.Key)}__");
            lines.AddRange(stack.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(ContainerLine));
        }

        return WithinLimit(header, lines, "lines");
    }

    public static string Stacks(ForestEnvironment environment, IReadOnlyList<ForestStack> stacks)
    {
        var header = $"**{Markdown.Escape(environment.Name)}** · {Count(stacks.Count, "stack")}";
        var lines = stacks.OrderBy(s => s.Name, StringComparer.Ordinal).Select(StackLine).ToList();
        return lines.Count == 0 ? header : WithinLimit(header, lines, "stacks");
    }

    public static string StackLine(ForestStack stack)
    {
        var deployed = stack.DeployedCommit is { } commit
            ? $"deployed {Markdown.Code(commit[..Math.Min(12, commit.Length)])}"
            : "not deployed";
        var actions = (stack.CanRestart, stack.CanTakeDown) switch
        {
            _ when stack.IsSystem => "system stack",
            (true, true) => "restart, down",
            (true, false) => "restart",
            (false, true) => "down",
            _ => "view only",
        };
        return $"{(stack.DeployedCommit is null ? "⚪" : "📦")} {Markdown.Code(stack.Name)} — {deployed} · {actions}";
    }

    /// <summary>
    /// The header followed by the end of the command's output in a code block, as much as fits in one message:
    /// when compose fails, the last lines say why.
    /// </summary>
    public static string WithOutput(string header, IReadOnlyList<string> output)
    {
        const string Open = "\n```\n";
        const string Close = "\n```";

        // A code block cannot escape its own fence; a zero-width space keeps compose output from closing it early.
        var lines = output
            .Select(l => l.Trim().Replace("```", "`​``", StringComparison.Ordinal))
            .Where(l => l.Length > 0)
            .ToList();
        var budget = MaxMessageLength - header.Length - Open.Length - Close.Length;
        if (lines.Count == 0 || budget <= 1)
        {
            return header;
        }

        var kept = new List<string>();
        var used = 0;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            // Room for the "… N earlier lines" note while lines are still left out.
            var note = i > 0 ? $"… {i} earlier lines".Length + 1 : 0;
            var cost = lines[i].Length + (kept.Count > 0 ? 1 : 0);
            if (used + cost + note > budget)
            {
                break;
            }

            kept.Insert(0, lines[i]);
            used += cost;
        }

        if (kept.Count == 0)
        {
            // The last line does not fit together with the note: keep as much of its end as fits.
            var last = lines[^1];
            kept.Add("…" + last[^Math.Min(last.Length, budget - 1)..]);
        }
        else if (kept.Count < lines.Count)
        {
            kept.Insert(0, $"… {lines.Count - kept.Count} earlier lines");
        }

        return header + Open + string.Join('\n', kept) + Close;
    }

    public static string ContainerLine(ForestContainer container)
    {
        var line = $"{StateIcon(container.State)} {Markdown.Code(container.Name)} — {Markdown.Escape(Status(container))}";
        return container.CanControl ? line : line + " · view only";
    }

    public static string StateIcon(ContainerState state) => state switch
    {
        ContainerState.Running => "🟢",
        ContainerState.Created or ContainerState.Restarting => "🟡",
        ContainerState.Paused => "⏸️",
        ContainerState.Exited or ContainerState.Dead => "🔴",
        _ => "⚪",
    };

    private static string Status(ForestContainer container) =>
        string.IsNullOrWhiteSpace(container.StatusText) ? container.State.ToString().ToLowerInvariant() : container.StatusText;

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>Joins the header and as many lines as fit, then says how many were left out.</summary>
    internal static string WithinLimit(string header, IReadOnlyList<string> lines, string noun)
    {
        var text = new StringBuilder(header);
        for (var i = 0; i < lines.Count; i++)
        {
            var omitted = $"\n…and {lines.Count - i} more {noun}.";
            var fitsWithTail = text.Length + 1 + lines[i].Length + (i == lines.Count - 1 ? 0 : omitted.Length) <= MaxMessageLength;
            if (!fitsWithTail)
            {
                return text.Append(omitted).ToString();
            }

            text.Append('\n').Append(lines[i]);
        }

        return text.ToString();
    }
}
