using ContainerForest.Agent.Configuration;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Stacks;

/// <summary>
/// Looks for symbolic links on the way to a host path, as the Docker daemon resolves a bind-mount source (ADR-0012).
/// A container that may write somewhere can leave a link there, and a later mount of a path through it would reach
/// whatever the link points at, `/` included; the aggregator's checks see only the path's text.
/// </summary>
/// <remarks>
/// The agent runs in a container: it sees the host through <see cref="AgentOptions.HostRoot"/> when the host's root is
/// mounted there, and otherwise only its own directories, which are mounted at the same path as on the host.
/// </remarks>
public sealed class HostPathInspector(IOptions<AgentOptions> options)
{
    /// <summary>What was found on the way to <paramref name="hostPath"/>; null when nothing is wrong with it.</summary>
    /// <param name="hostPath">An absolute, normalized path on the host.</param>
    /// <param name="agentLinks">
    /// The links the agent itself puts in the stack directory for the bundle's secret files (<see cref="SecretLinks"/>):
    /// such a link pointing exactly where the agent points it is not a trap.
    /// </param>
    public PathProblem? Inspect(string hostPath, IReadOnlyDictionary<string, string>? agentLinks = null)
    {
        if (OperatingSystem.IsWindows())
        {
            // Development only: Docker Desktop resolves bind mounts in its own VM.
            return null;
        }

        if (Base(hostPath) is not { } start)
        {
            return new PathProblem(hostPath, Link: null);
        }

        // Without the host's root the agent sees only its own directories, mounted at their host paths; the directories
        // above them are the container's, not the host's, and only root on the host could have changed them anyway.
        var current = start;
        foreach (var segment in hostPath[start.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            var seen = View(current);
            if (new FileInfo(seen).LinkTarget is { } target)
            {
                // A secret file's link is the last component: it points at a file on the agent's tmpfs.
                if (agentLinks is not null && agentLinks.TryGetValue(current, out var expected) && target == expected)
                {
                    break;
                }

                return new PathProblem(hostPath, current);
            }

            // A missing component is created by Docker as a plain directory; a file ends the walk.
            if (!Directory.Exists(seen))
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// The host directory the walk starts below: "" (the root) with the host's root mounted, otherwise the agent's own
    /// directory that holds the path; null when the agent cannot see the path.
    /// </summary>
    private string? Base(string hostPath)
    {
        var settings = options.Value;
        if (settings.HostRoot is not null)
        {
            return "";
        }

        return new[] { settings.DataDirectory, settings.StacksRoot, settings.SecretsDirectory }
            .Select(Path.TrimEndingDirectorySeparator)
            .Where(directory => IsUnder(hostPath, directory))
            .OrderByDescending(directory => directory.Length)
            .FirstOrDefault();
    }

    /// <summary>Where the agent sees <paramref name="hostPath"/>.</summary>
    private string View(string hostPath) =>
        options.Value.HostRoot is { } root ? Path.Join(root, hostPath) : hostPath;

    private static bool IsUnder(string path, string directory) =>
        path == directory || path.StartsWith(directory + "/", StringComparison.Ordinal);

    /// <summary>
    /// The links <see cref="StackFileStore"/> puts in the stack directory for the secret files of
    /// <paramref name="layout"/>: host path of the link → its target on the secrets tmpfs. Empty on Windows, where secret
    /// files are written into the stack directory.
    /// </summary>
    public IReadOnlyDictionary<string, string> SecretLinks(StackLayout layout)
    {
        if (OperatingSystem.IsWindows())
        {
            return new Dictionary<string, string>();
        }

        var settings = options.Value;
        var stackDirectory = Path.Combine(settings.StacksRoot, layout.StackName);
        var secrets = Path.Combine(settings.SecretsDirectory, layout.StackName);
        return layout.SecretFiles.ToDictionary(
            f => StackLayout.ResolveIn(stackDirectory, f.Path),
            f => StackLayout.ResolveIn(secrets, f.Path),
            StringComparer.Ordinal);
    }

    /// <summary>Checks that a path from the aggregator is one this class can inspect without surprises.</summary>
    public static bool IsAcceptable(string path) =>
        path.Length is > 0 and <= 4096
        && path[0] == '/'
        && !path.Any(char.IsControl)
        && path.Split('/').Skip(1).All(segment => segment is not ("" or "." or ".."));
}

/// <param name="Link">The first component that is a symbolic link; null when the path cannot be inspected at all.</param>
public sealed record PathProblem(string Path, string? Link)
{
    public string Describe() => Link is null
        ? $"bind mount source '{Path}' cannot be checked: the agent does not see it. Mount the host's root into the agent " +
          "read-only (/:/host:ro,rslave and CF_AGENT__HOSTROOT=/host, as in deploy/agent/compose.yaml)."
        : Link == Path
            ? $"bind mount source '{Path}' is a symbolic link."
            : $"bind mount source '{Path}' goes through the symbolic link '{Link}'.";
}
