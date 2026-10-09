namespace ContainerForest.Cli.Output;

/// <summary>A plain-text table: columns padded to their widest cell, two spaces apart, no borders to strip in scripts.</summary>
public sealed class Table(params string[] headers)
{
    private const string Gap = "  ";
    private const string Missing = "-";

    private readonly List<string[]> rows = [];

    public int RowCount => rows.Count;

    /// <summary>Adds a row; a null or empty cell shows as "-". Missing trailing cells are empty.</summary>
    public Table AddRow(params string?[] cells)
    {
        if (cells.Length > headers.Length)
        {
            throw new ArgumentException($"A row of {cells.Length} cells does not fit {headers.Length} columns.", nameof(cells));
        }

        rows.Add([.. headers.Select((_, i) => i < cells.Length && !string.IsNullOrEmpty(cells[i]) ? Clean(cells[i]!) : Missing)]);
        return this;
    }

    public void Render(TextWriter writer)
    {
        var widths = headers.Select((header, i) => rows.Select(row => row[i].Length).Append(header.Length).Max()).ToArray();
        WriteRow(writer, headers, widths);
        foreach (var row in rows)
        {
            WriteRow(writer, row, widths);
        }
    }

    private static void WriteRow(TextWriter writer, IReadOnlyList<string> cells, int[] widths)
    {
        // The last column is not padded, so lines carry no trailing spaces.
        var line = string.Join(Gap, cells.Select((cell, i) => i == cells.Count - 1 ? cell : cell.PadRight(widths[i])));
        writer.WriteLine(line);
    }

    // A cell is one line: a name from the server must not break the table or move the cursor.
    private static string Clean(string cell) => new(cell.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
