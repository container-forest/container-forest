using System.Globalization;

namespace ContainerForest.Agent.Metrics;

/// <summary>Parsers for cgroup v2 interface files (Documentation/admin-guide/cgroup-v2.rst).</summary>
public static class CgroupParsers
{
    /// <summary>"key value" lines, as in cpu.stat and memory.stat.</summary>
    public static Dictionary<string, ulong> ParseFlatKeyed(string content)
    {
        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var line in Lines(content))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                result[parts[0]] = value;
            }
        }

        return result;
    }

    /// <summary>cpu.max: "$MAX $PERIOD", where $MAX is "max" without a limit. Returns cores; 0 = no limit.</summary>
    public static double ParseCpuMax(string content)
    {
        var parts = content.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var quota)
            && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var period)
            && period > 0
                ? (double)quota / period
                : 0;
    }

    /// <summary>memory.max and similar single values: a number, or "max" for no limit (0).</summary>
    public static ulong ParseSingleOrMax(string content) =>
        ulong.TryParse(content.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>io.stat: "MAJ:MIN rbytes=1 wbytes=2 rios=3 ..." per device; returns the sums of rbytes and wbytes.</summary>
    public static (ulong Read, ulong Write) ParseIoStat(string content)
    {
        ulong read = 0, write = 0;
        foreach (var line in Lines(content))
        {
            foreach (var field in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                var eq = field.IndexOf('=');
                if (eq <= 0 || !ulong.TryParse(field.AsSpan(eq + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                {
                    continue;
                }

                switch (field.AsSpan(0, eq))
                {
                    case "rbytes":
                        read += value;
                        break;
                    case "wbytes":
                        write += value;
                        break;
                }
            }
        }

        return (read, write);
    }

    /// <summary>
    /// /proc/&lt;pid&gt;/cgroup on a cgroup v2 host: "0::/system.slice/docker-&lt;id&gt;.scope". Returns the path relative to
    /// the cgroup root, or null when it is outside the reader's cgroup namespace ("/../..."), or there is no v2 line.
    /// </summary>
    public static string? ParseProcCgroup(string content)
    {
        foreach (var line in Lines(content))
        {
            if (line.StartsWith("0::/", StringComparison.Ordinal))
            {
                var path = line[4..];
                return path.Split('/').Any(segment => segment is ".." or ".") ? null : path;
            }
        }

        return null;
    }

    private static IEnumerable<string> Lines(string content) =>
        content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
