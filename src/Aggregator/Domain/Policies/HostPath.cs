namespace ContainerForest.Aggregator.Domain.Policies;

/// <summary>
/// Absolute paths on a Linux host, compared by segments and case-sensitively: "/mnt/media2" is not under "/mnt/media".
/// </summary>
public static class HostPath
{
    /// <summary>"/srv/media/" → "/srv/media"; null for a relative path or one with empty, '.' or '..' segments.</summary>
    public static string? TryNormalize(string path)
    {
        if (path.Length is 0 or > 4096 || path[0] != '/' || path.Any(char.IsControl))
        {
            return null;
        }

        if (path == "/")
        {
            return path;
        }

        var trimmed = path.TrimEnd('/');
        var segments = trimmed.Split('/').Skip(1).ToList();
        return segments.All(s => s is not ("" or "." or "..")) ? trimmed : null;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="directory"/> or lies under it.</summary>
    public static bool IsAtOrUnder(string path, string directory) =>
        directory == "/" || path == directory || path.StartsWith(directory + "/", StringComparison.Ordinal);

    /// <summary>Whether one of the paths contains the other: mounting either reaches into the other.</summary>
    public static bool Overlaps(string path, string other) => IsAtOrUnder(path, other) || IsAtOrUnder(other, path);
}
