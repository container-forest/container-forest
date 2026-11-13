using System.Text.RegularExpressions;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Docker;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Stacks;

/// <summary>A compose project run by hand in one of <see cref="AgentOptions.UnmanagedStackRoots"/> (ADR-0016).</summary>
/// <param name="Name">The compose project name.</param>
/// <param name="Directory">The project directory, absolute.</param>
/// <param name="ConfigFiles">The compose files, absolute, in the order compose reads them.</param>
/// <param name="EnvFiles">Files compose was given with --env-file; empty for the project directory's own .env.</param>
/// <param name="IsSystem">One of its containers is a part of container-forest itself.</param>
/// <param name="HasContainers">Found by its containers; false for a directory with a compose file only.</param>
public sealed record UnmanagedStackLocation(
    string Name,
    string Directory,
    IReadOnlyList<string> ConfigFiles,
    IReadOnlyList<string> EnvFiles,
    bool IsSystem,
    bool HasContainers);

/// <summary>
/// Finds unmanaged stacks: compose projects whose containers' labels put them in an unmanaged stack root, and
/// directories in those roots with a compose file, so a stack that was taken down can be brought up again.
/// Everything comes from the host, never from the aggregator: it names a stack, and only a stack found here runs.
/// </summary>
public sealed partial class UnmanagedStackFinder(
    IDockerGateway docker, IOptions<AgentOptions> options, ILogger<UnmanagedStackFinder> logger)
{
    /// <summary>The names compose looks for in a project directory, in its order of preference.</summary>
    public static readonly IReadOnlyList<string> ComposeFileNames =
        ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    /// <summary>How deep below a root the scan looks: /srv/docker/gitea/runner is two levels down.</summary>
    public const int MaxScanDepth = 3;

    /// <summary>A root with more directories than this is not scanned further; the stacks found by labels stay.</summary>
    public const int MaxScannedDirectories = 2000;

    /// <summary>Compose files and .env above this size are not read for the project name.</summary>
    private const long MaxNameSourceBytes = 1024 * 1024;

    public bool Enabled => options.Value.UnmanagedStackRoots.Count > 0;

    /// <summary>Every unmanaged stack on the host, by name.</summary>
    public async Task<IReadOnlyList<UnmanagedStackLocation>> FindAllAsync(CancellationToken cancellationToken)
    {
        var roots = options.Value.NormalizedUnmanagedStackRoots;
        if (roots.Count == 0)
        {
            return [];
        }

        var stacks = new Dictionary<string, UnmanagedStackLocation>(StringComparer.Ordinal);
        foreach (var project in FromContainers(await docker.ListComposeContainersAsync(cancellationToken), roots))
        {
            stacks[project.Name] = project;
        }

        // Directories that already have a project by its labels are that project, whatever the scan would call it.
        var claimed = new HashSet<string>(stacks.Values.Select(s => s.Directory), PathComparer);
        foreach (var root in roots)
        {
            foreach (var found in Scan(root, cancellationToken))
            {
                if (claimed.Add(found.Directory) && !stacks.ContainsKey(found.Name))
                {
                    stacks[found.Name] = found;
                }
            }
        }

        return stacks.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <returns>The stack, or null when there is none by that name.</returns>
    public async Task<UnmanagedStackLocation?> FindAsync(string name, CancellationToken cancellationToken) =>
        (await FindAllAsync(cancellationToken)).FirstOrDefault(s => s.Name == name);

    /// <summary>Groups compose containers into projects and keeps those that live wholly in a root.</summary>
    internal IEnumerable<UnmanagedStackLocation> FromContainers(IReadOnlyList<ComposeContainer> containers, IReadOnlyList<string> roots)
    {
        var stacksRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.StacksRoot));
        foreach (var project in containers.GroupBy(c => c.Info.StackName, StringComparer.Ordinal))
        {
            // Compose labels every container of a run alike; the newest one tells how the project was last brought up.
            var latest = project.OrderByDescending(c => c.Info.CreatedUnixMs).First();
            var directory = latest.WorkingDirectory;
            if (!IsUsablePath(directory) || AgentOptions.PathContains(stacksRoot, Full(directory)))
            {
                continue;
            }

            var root = roots.FirstOrDefault(r => AgentOptions.PathContains(r, Full(directory)));
            if (root is null)
            {
                continue;
            }

            // A file outside the roots is not the administrator's to hand to the panel.
            if (latest.ConfigFiles.Count == 0
                || !latest.ConfigFiles.Concat(latest.EnvFiles).All(f => IsUsablePath(f) && roots.Any(r => AgentOptions.PathContains(r, Full(f)))))
            {
                logger.LogDebug("Compose project {Project} uses files outside the unmanaged stack roots; skipped", project.Key);
                continue;
            }

            if (!IsValidName(project.Key))
            {
                continue;
            }

            yield return new UnmanagedStackLocation(
                project.Key,
                Full(directory),
                latest.ConfigFiles.Select(Full).ToList(),
                latest.EnvFiles.Select(Full).ToList(),
                project.Any(c => c.Info.System),
                HasContainers: true);
        }
    }

    /// <summary>Directories in the root, the root included, with a compose file; it does not look below one that has one.</summary>
    internal IEnumerable<UnmanagedStackLocation> Scan(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            logger.LogWarning("Unmanaged stack root {Root} does not exist in the agent; mount it at the same path as on the host", root);
            yield break;
        }

        var pending = new Queue<(string Directory, int Depth)>([(root, 0)]);
        var visited = 0;
        while (pending.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > MaxScannedDirectories)
            {
                logger.LogWarning("Unmanaged stack root {Root} has more than {Max} directories; the rest is not scanned", root, MaxScannedDirectories);
                yield break;
            }

            var composeFile = ComposeFileNames.Select(n => Path.Combine(current.Directory, n)).FirstOrDefault(IsRegularFile);
            if (composeFile is not null)
            {
                if (ProjectNameOf(current.Directory, composeFile) is { } name)
                {
                    yield return new UnmanagedStackLocation(name, current.Directory, [composeFile], [], IsSystem: false, HasContainers: false);
                }

                continue;
            }

            if (current.Depth == MaxScanDepth)
            {
                continue;
            }

            foreach (var child in Subdirectories(current.Directory))
            {
                pending.Enqueue((child, current.Depth + 1));
            }
        }
    }

    /// <summary>
    /// The project name compose gives the directory: COMPOSE_PROJECT_NAME from its .env, then the compose file's
    /// top-level name, then the directory's name made a valid project name.
    /// </summary>
    /// <returns>Null when no valid name comes out, or the name depends on interpolation.</returns>
    public static string? ProjectNameOf(string directory, string composeFile)
    {
        var fromEnv = FirstMatch(Path.Combine(directory, ".env"), EnvProjectName());
        if (fromEnv is not null)
        {
            return IsValidName(fromEnv) ? fromEnv : null;
        }

        var fromFile = FirstMatch(composeFile, TopLevelName());
        if (fromFile is not null)
        {
            return !fromFile.Contains('$') && IsValidName(fromFile) ? fromFile : null;
        }

        var normalized = NormalizeName(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)));
        return IsValidName(normalized) ? normalized : null;
    }

    /// <summary>Compose's own normalization: lower case, only [a-z0-9_-], no leading '_' or '-'.</summary>
    public static string NormalizeName(string name) =>
        DisallowedNameCharacters().Replace(name.ToLowerInvariant(), "").TrimStart('_', '-');

    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= StackName.MaxLength && ValidName().IsMatch(name);

    private static string? FirstMatch(string path, Regex pattern)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Length > MaxNameSourceBytes)
            {
                return null;
            }

            var match = pattern.Match(File.ReadAllText(path));
            return match.Success ? match.Groups["name"].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private IEnumerable<string> Subdirectories(string directory)
    {
        try
        {
            // Symbolic links are not followed: a container that can write in a root must not lead the scan elsewhere.
            return new DirectoryInfo(directory).EnumerateDirectories()
                .Where(d => d.LinkTarget is null && !d.Name.StartsWith('.'))
                .Select(d => d.FullName)
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("Cannot list {Directory}: {Reason}", directory, ex.Message);
            return [];
        }
    }

    private static bool IsRegularFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.LinkTarget is null;
    }

    private static bool IsUsablePath(string path) =>
        !string.IsNullOrEmpty(path) && !path.Any(char.IsControl) && Path.IsPathFullyQualified(path);

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();

    [GeneratedRegex("[^-_a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex DisallowedNameCharacters();

    // A top-level key starts the line; quotes and a trailing comment are allowed.
    [GeneratedRegex(@"^name:[ \t]*[""']?(?<name>[^""'\s#]+)[""']?[ \t]*(#.*)?\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TopLevelName();

    [GeneratedRegex(@"^[ \t]*COMPOSE_PROJECT_NAME[ \t]*=[ \t]*[""']?(?<name>[^""'\s#]+)[""']?[ \t]*(#.*)?\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex EnvProjectName();
}
