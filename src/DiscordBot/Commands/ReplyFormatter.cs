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
