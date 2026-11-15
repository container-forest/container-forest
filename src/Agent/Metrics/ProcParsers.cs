using System.Globalization;

namespace ContainerForest.Agent.Metrics;

/// <summary>Parsers for the host's /proc files (proc(5)).</summary>
public static class ProcParsers
{
    /// <summary>The first line of /proc/stat: "cpu user nice system idle iowait irq softirq steal guest guest_nice".</summary>
    /// <remarks>guest time is already counted in user, so it is not added again.</remarks>
    public static CpuTimes? ParseCpuTimes(string content)
    {
        var line = content.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(Number).ToArray();
        if (v.Length < 4)
        {
            return null;
        }

        ulong At(int i) => i < v.Length ? v[i] : 0;
        return new CpuTimes(
            User: At(0) + At(1),
            System: At(2) + At(5) + At(6),
            Idle: At(3),
            IoWait: At(4),
            Steal: At(7));
    }

    /// <summary>The number of "cpuN" lines of /proc/stat.</summary>
    public static uint CountCpus(string content) =>
        (uint)content.Split('\n').Count(l => l.Length > 3 && l.StartsWith("cpu", StringComparison.Ordinal) && char.IsAsciiDigit(l[3]));

    /// <summary>/proc/meminfo values in bytes ("MemTotal:  16303548 kB").</summary>
    public static Dictionary<string, ulong> ParseMemInfo(string content)
    {
        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                result[line[..colon]] = parts.Length >= 2 && parts[1] == "kB" ? value * 1024 : value;
            }
        }

        return result;
    }

    /// <summary>/proc/loadavg: "0.20 0.18 0.12 1/80 11206".</summary>
    public static (double One, double Five, double Fifteen) ParseLoadAvg(string content)
    {
        var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double At(int i) => i < parts.Length && double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return (At(0), At(1), At(2));
    }

    /// <summary>/proc/uptime: "350735.47 234388.90", seconds since boot.</summary>
    public static long ParseUptime(string content) =>
        double.TryParse(content.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (long)v
            : 0;

    /// <summary>
    /// /proc/&lt;pid&gt;/net/dev: two header lines, then "  eth0: rx_bytes rx_packets ... (8 fields) tx_bytes ...".
    /// </summary>
    public static Dictionary<string, NetworkCounters> ParseNetDev(string content)
    {
        var result = new Dictionary<string, NetworkCounters>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n').Skip(2))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var fields = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 9)
            {
                result[line[..colon].Trim()] = new NetworkCounters(Number(fields[0]), Number(fields[8]));
            }
        }

        return result;
    }

    /// <summary>
    /// /proc/diskstats: "major minor name reads merged sectors_read ms writes merged sectors_written ...". Returns bytes
    /// read and written per device name (sectors are always 512 bytes there).
    /// </summary>
    public static Dictionary<string, (ulong Read, ulong Write)> ParseDiskStats(string content)
    {
        var result = new Dictionary<string, (ulong, ulong)>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 10)
            {
                result[f[2]] = (Number(f[5]) * 512, Number(f[9]) * 512);
            }
        }

        return result;
    }

    /// <summary>/proc/&lt;pid&gt;/mounts lines: device, mount point (spaces as \040), type.</summary>
    public static IEnumerable<(string Device, string MountPoint, string Type)> ParseMounts(string content)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(' ');
            if (f.Length >= 3)
            {
                yield return (Unescape(f[0]), Unescape(f[1]), f[2]);
            }
        }
    }

    // The kernel escapes space, tab, newline and backslash in mount fields as three octal digits.
    private static string Unescape(string field) =>
        field.Contains('\\', StringComparison.Ordinal)
            ? System.Text.RegularExpressions.Regex.Replace(field, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString())
            : field;

    private static ulong Number(string s) =>
        ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
