using System.Globalization;
using Microsoft.Kiota.Abstractions.Serialization;

namespace ContainerForest.Cli.Output;

public enum OutputFormat
{
    /// <summary>Tables and sentences for people.</summary>
    Table,

    /// <summary>The server's JSON, for scripts and jq.</summary>
    Json,
}

/// <summary>
/// Where commands write. Results go to standard output, as a table or as JSON (<c>-o json</c>); progress and
/// notes go to standard error, so a pipe gets only the result.
/// </summary>
public sealed class CliOutput(TextWriter output, TextWriter error, OutputFormat format)
{
    public OutputFormat Format { get; } = format;

    public TextWriter Out { get; } = output;

    public TextWriter Error { get; } = error;

    public bool IsJson => Format == OutputFormat.Json;

    /// <summary>A list: as a table, or as a JSON array of the server's objects.</summary>
    public async Task ListAsync<T>(IReadOnlyCollection<T> items, Func<IReadOnlyCollection<T>, Table> toTable, string emptyText)
        where T : IParsable
    {
        if (IsJson)
        {
            await Out.WriteLineAsync(await KiotaJsonSerializer.SerializeAsStringAsync(items));
            return;
        }

        if (items.Count == 0)
        {
            await Error.WriteLineAsync(emptyText);
            return;
        }

        toTable(items).Render(Out);
    }

    /// <summary>One object: as fields for people, or as the server's JSON.</summary>
    public async Task ItemAsync<T>(T item, Func<T, IEnumerable<(string Label, string? Value)>> fields)
        where T : IParsable
    {
        if (IsJson)
        {
            await Out.WriteLineAsync(await KiotaJsonSerializer.SerializeAsStringAsync(item));
            return;
        }

        var lines = fields(item).ToList();
        var width = lines.Max(l => l.Label.Length) + 1;
        foreach (var (label, value) in lines)
        {
            await Out.WriteLineAsync($"{(label + ":").PadRight(width)} {(string.IsNullOrEmpty(value) ? "-" : value)}");
        }
    }

    /// <summary>What a command did, for people; a script reads the exit code instead.</summary>
    public void Done(string text)
    {
        if (!IsJson)
        {
            Out.WriteLine(text);
        }
    }

    /// <summary>Progress and notes on standard error, in both formats.</summary>
    public void Note(string text) => Error.WriteLine(text);

    /// <summary>A secret shown once (a new token). Always on standard output, alone on its line, in both formats.</summary>
    public void Secret(string text) => Out.WriteLine(text);

    /// <summary>A time in the user's time zone, e.g. "2026-10-09 14:05".</summary>
    public static string Time(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";

    public static string YesNo(bool? value) => value == true ? "yes" : "no";

    /// <summary>The first 12 characters of a commit, as git shows them.</summary>
    public static string ShortCommit(string? commit) => commit is { Length: > 12 } ? commit[..12] : commit ?? "-";
}
