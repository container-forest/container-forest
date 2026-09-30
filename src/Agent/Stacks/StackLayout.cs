using ContainerForest.Agent.Commands;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Stacks;

/// <summary>
/// The validated files of a <see cref="DeployStack"/> bundle. Everything that can be checked without touching the disk
/// is checked here, before anything is written: a bundle is either written as a whole or rejected.
/// </summary>
public sealed class StackLayout
{
    /// <summary>The compose file the agent passes to compose with -f; the aggregator always sends it under this name.</summary>
    public const string ComposeFileName = "compose.yaml";

    public const int MaxPathLength = 1024;

    public const UnixFileMode RegularMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public const UnixFileMode ExecutableMode =
        RegularMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private StackLayout(string stackName, IReadOnlyList<StackLayoutFile> files)
    {
        StackName = stackName;
        Files = files;
    }

    public string StackName { get; }

    public IReadOnlyList<StackLayoutFile> Files { get; }

    public IEnumerable<StackLayoutFile> RegularFiles => Files.Where(f => !f.Secret);

    public IEnumerable<StackLayoutFile> SecretFiles => Files.Where(f => f.Secret);

    /// <exception cref="InvalidCommandArgumentException">A name, path or mode is not acceptable.</exception>
    public static StackLayout Create(string stackName, IEnumerable<StackFile> files)
    {
        Stacks.StackName.Validate(stackName);

        // Windows file names are case-insensitive, so "App.env" and "app.env" would be one file there.
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paths = new HashSet<string>(comparer);
        var result = new List<StackLayoutFile>();
        foreach (var file in files)
        {
            var path = ValidatePath(file.Path);
            if (!paths.Add(path))
            {
                throw new InvalidCommandArgumentException($"File '{path}' appears in the bundle more than once.");
            }

            result.Add(new StackLayoutFile(path, file.Content.Memory, ToMode(path, file.Mode), file.Secret));
        }

        // "config" as a file and "config/app.yaml" cannot both exist.
        foreach (var path in paths)
        {
            for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                if (paths.Contains(path[..slash]))
                {
                    throw new InvalidCommandArgumentException($"'{path[..slash]}' is both a file and a directory in the bundle.");
                }
            }
        }

        var compose = result.Find(f => comparer.Equals(f.Path, ComposeFileName))
            ?? throw new InvalidCommandArgumentException($"The bundle has no '{ComposeFileName}' at the stack directory root.");
        if (compose.Secret)
        {
            throw new InvalidCommandArgumentException($"'{ComposeFileName}' must not be a secret file.");
        }

        return new StackLayout(stackName, result);
    }

    /// <summary>
    /// Returns the full path of <paramref name="relativePath"/> in <paramref name="directory"/>. Paths are already
    /// checked by <see cref="Create"/>; this is the last check before a write, on the path the file system will see.
    /// </summary>
    /// <exception cref="InvalidCommandArgumentException">The path leads outside the directory.</exception>
    public static string ResolveIn(string directory, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, comparison) || full.Length == root.Length)
        {
            throw new InvalidCommandArgumentException($"File path '{relativePath}' leads outside the stack directory.");
        }

        return full;
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidCommandArgumentException("A file in the bundle has no path.");
        }

        // The path goes into error messages, so it is shown only once it is known to be printable.
        if (path.Length > MaxPathLength || path.Any(char.IsControl))
        {
            throw new InvalidCommandArgumentException(
                $"File paths must be at most {MaxPathLength} characters without control characters.");
        }

        // '\' and ':' would make a Windows path absolute ("C:x", "\x") or name an alternate data stream;
        // on Linux they are legal but never needed in a stack, and ':' breaks compose's short volume syntax.
        if (path[0] == '/' || path.Contains('\\') || path.Contains(':'))
        {
            throw new InvalidCommandArgumentException($"File path '{path}' must be relative, with '/' separators and without ':'.");
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                throw new InvalidCommandArgumentException($"File path '{path}' must not contain empty, '.' or '..' segments.");
            }
        }

        return path;
    }

    // The aggregator sends 0644 or 0755 (0 is read as 0644). Anything else, such as setuid bits, has no use in a stack.
    private static UnixFileMode ToMode(string path, uint mode) => mode switch
    {
        0 or (uint)RegularMode => RegularMode,
        (uint)ExecutableMode => ExecutableMode,
        _ => throw new InvalidCommandArgumentException($"File '{path}' has mode {Convert.ToString(mode, 8)}; only 644 and 755 are allowed."),
    };
}

/// <param name="Path">Relative to the stack directory, with '/' separators.</param>
public sealed record StackLayoutFile(string Path, ReadOnlyMemory<byte> Content, UnixFileMode Mode, bool Secret);
