using System.Text.Json;
using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Configuration;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Stacks;

/// <summary>
/// Writes bundles into stack directories on the host.
/// </summary>
/// <remarks>
/// <para>
/// The stack directory stays in place and only the files of the bundle change: containers bind-mount paths inside it
/// (./data, ./nginx.conf), and moving the directory away would leave running containers on the old copy and lose
/// their data with it. Every file is prepared in &lt;stack&gt;.tmp and renamed over its target, so each file changes
/// atomically. Files of the previous bundle that are not in the new one are removed; the <see cref="StackManifest"/>
/// tells them apart from files the containers created. The previous bundle is copied to &lt;stack&gt;.prev for a rollback.
/// </para>
/// <para>
/// Secret files go to &lt;SecretsDirectory&gt;/&lt;stack&gt; on tmpfs, replaced as a whole directory (.tmp → current → .prev),
/// and the stack directory gets a symbolic link to each of them at the file's path. On Windows (development only)
/// secret files are written into the stack directory.
/// </para>
/// </remarks>
public sealed class StackFileStore(IOptions<AgentOptions> options, ILogger<StackFileStore> logger) : IStackFileStore
{
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public bool Exists(string stackName) => Directory.Exists(StackDirectory(stackName));

    public async Task<string> WriteAsync(StackLayout layout, CancellationToken cancellationToken)
    {
        var name = layout.StackName;
        var directory = StackDirectory(name);
        var staging = directory + ".tmp";
        var secrets = SecretsDirectory(name);

        var files = layout.Files.Select(f => new PlannedFile(
            f, StackLayout.ResolveIn(directory, f.Path), StackLayout.ResolveIn(staging, f.Path), StackLayout.ResolveIn(secrets, f.Path))).ToList();

        // Every check against the host comes before the first change, so a conflict leaves the stack as it was.
        foreach (var file in files)
        {
            CheckTarget(directory, file.Source.Path, file.Target);
        }

        var previous = await ReadManifestAsync(name, cancellationToken);
        var next = StackManifest.Of(layout);
        try
        {
            DeleteDirectory(staging);
            DeleteDirectory(secrets + ".tmp");
            Directory.CreateDirectory(directory);
            await StageAsync(name, files, cancellationToken);
            await BackUpAsync(name, previous, cancellationToken);

            // From here on files change. The manifest first lists both versions, so an interrupted write
            // still lets the next deployment remove whatever this one leaves behind.
            await (previous is null ? next : previous.Union(next, PathComparer)).WriteAsync(ManifestPath(name), cancellationToken);
            ReplaceDirectory(secrets + ".tmp", secrets);
            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
                File.Move(file.Staged, file.Target, overwrite: true);
            }

            RemoveStale(directory, previous, next);
            await next.WriteAsync(ManifestPath(name), cancellationToken);
        }
        finally
        {
            DeleteDirectory(staging);
            DeleteDirectory(secrets + ".tmp");
        }

        logger.LogInformation(
            "Wrote {Count} file(s) of stack {Stack} to {Directory}, {Secrets} of them secret",
            files.Count, name, directory, files.Count(f => f.Source.Secret));
        return directory;
    }

    public void DiscardPreviousSecrets(string stackName) => DeleteDirectory(SecretsDirectory(stackName) + ".prev");

    public void DeleteSecrets(string stackName)
    {
        DeleteDirectory(SecretsDirectory(stackName));
        DeleteDirectory(SecretsDirectory(stackName) + ".prev");
    }

    private string StackDirectory(string stackName) => Path.Combine(options.Value.StacksRoot, stackName);

    private string SecretsDirectory(string stackName) => Path.Combine(options.Value.SecretsDirectory, stackName);

    private string ManifestPath(string stackName) => StackDirectory(stackName) + ".manifest.json";

    /// <summary>
    /// A container can create anything in a directory it bind-mounts, including a symbolic link to "/":
    /// the agent, which runs as root, must never write or delete through such a link.
    /// </summary>
    /// <exception cref="StackFileConflictException">The path cannot be written safely.</exception>
    private static void CheckTarget(string directory, string path, string target)
    {
        if (LinkedOrFileAncestor(directory, target) is { } reason)
        {
            throw new StackFileConflictException(path, reason);
        }

        if (Directory.Exists(target) && new DirectoryInfo(target).LinkTarget is null)
        {
            throw new StackFileConflictException(path, "a directory with this name exists on the host.");
        }
    }

    /// <returns>Why a directory between <paramref name="root"/> and <paramref name="path"/> is unusable, or null.</returns>
    private static string? LinkedOrFileAncestor(string root, string path)
    {
        var relative = Path.GetRelativePath(root, Path.GetDirectoryName(path)!);
        if (relative == ".")
        {
            return null;
        }

        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            var info = new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                return $"'{Path.GetRelativePath(root, current)}' is a symbolic link on the host.";
            }

            if (info.Exists)
            {
                return $"'{Path.GetRelativePath(root, current)}' is a file on the host.";
            }

            if (!Directory.Exists(current))
            {
                return null;
            }
        }

        return null;
    }

    private async Task StageAsync(string stackName, List<PlannedFile> files, CancellationToken cancellationToken)
    {
        var secretCount = 0;
        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Staged)!);
            if (!file.Source.Secret)
            {
                await WriteFileAsync(file.Staged, file.Source, cancellationToken);
            }
            else if (OperatingSystem.IsWindows())
            {
                secretCount++;
                await WriteFileAsync(file.Staged, file.Source, cancellationToken);
            }
            else
            {
                // Content on tmpfs; the stack directory gets a link to where the file will be after the swap.
                var stagedSecrets = SecretsDirectory(stackName) + ".tmp";
                var content = StackLayout.ResolveIn(stagedSecrets, file.Source.Path);
                Directory.CreateDirectory(options.Value.SecretsDirectory);
                Directory.CreateDirectory(stagedSecrets, OwnerOnlyDirectory);
                Directory.CreateDirectory(Path.GetDirectoryName(content)!, OwnerOnlyDirectory);
                await WriteFileAsync(content, file.Source, cancellationToken);
                File.CreateSymbolicLink(file.Staged, file.Secret);
            }
        }

        if (secretCount > 0)
        {
            logger.LogWarning(
                "{Count} secret file(s) of stack {Stack} are written into the stack directory on disk: Windows has no secrets tmpfs",
                secretCount, stackName);
        }
    }

    private static async Task WriteFileAsync(string path, StackLayoutFile file, CancellationToken cancellationToken)
    {
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            // Owner-only until the content is complete; then exactly the bundle's mode, whatever the umask.
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (var stream = new FileStream(path, fileOptions))
        {
            await stream.WriteAsync(file.Content, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, file.Mode);
        }
    }

    /// <summary>Copies the previous bundle's regular files to &lt;stack&gt;.prev; secrets are not kept on disk.</summary>
    private async Task BackUpAsync(string stackName, StackManifest? previous, CancellationToken cancellationToken)
    {
        if (previous is null)
        {
            return;
        }

        var directory = StackDirectory(stackName);
        var building = directory + ".prev.tmp";
        DeleteDirectory(building);
        try
        {
            foreach (var entry in previous.Files.Where(f => !f.Secret))
            {
                if (TryResolveOwnFile(directory, entry.Path) is not { } source || !File.Exists(source))
                {
                    continue;
                }

                var target = StackLayout.ResolveIn(building, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }

            Directory.CreateDirectory(building);
            await new StackManifest(previous.Files.Where(f => !f.Secret).ToList())
                .WriteAsync(Path.Combine(building, ".manifest.json"), cancellationToken);
            ReplaceDirectory(building, directory + ".prev", keepPrevious: false);
        }
        finally
        {
            DeleteDirectory(building);
        }
    }

    private void RemoveStale(string directory, StackManifest? previous, StackManifest next)
    {
        if (previous is null)
        {
            return;
        }

        var kept = next.Files.Select(f => f.Path).ToHashSet(PathComparer);
        foreach (var entry in previous.Files.Where(f => !kept.Contains(f.Path)))
        {
            if (TryResolveOwnFile(directory, entry.Path) is not { } path)
            {
                continue;
            }

            var info = new FileInfo(path);
            if (info.Exists || info.LinkTarget is not null)
            {
                info.Delete();
                RemoveEmptyParents(directory, path);
            }
        }
    }

    /// <summary>
    /// Resolves a path from a manifest. The manifest is the agent's own file, but the path is checked again
    /// before anything is copied or deleted through it.
    /// </summary>
    private string? TryResolveOwnFile(string directory, string relativePath)
    {
        try
        {
            var path = StackLayout.ResolveIn(directory, relativePath);
            if (LinkedOrFileAncestor(directory, path) is { } reason)
            {
                logger.LogWarning("Skipping '{Path}' in {Directory}: {Reason}", relativePath, directory, reason);
                return null;
            }

            return path;
        }
        catch (InvalidCommandArgumentException)
        {
            logger.LogWarning("Skipping '{Path}' from the manifest of {Directory}: it leads outside the directory", relativePath, directory);
            return null;
        }
    }

    // Directories the bundle created and that are empty now; a directory a container still uses is not empty.
    private static void RemoveEmptyParents(string root, string path)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var parent = Path.GetDirectoryName(path);
             parent is not null && parent.Length > rootFull.Length && !Directory.EnumerateFileSystemEntries(parent).Any();
             parent = Path.GetDirectoryName(parent))
        {
            Directory.Delete(parent);
        }
    }

    private async Task<StackManifest?> ReadManifestAsync(string stackName, CancellationToken cancellationToken)
    {
        try
        {
            return await StackManifest.ReadAsync(ManifestPath(stackName), cancellationToken);
        }
        catch (JsonException ex)
        {
            // Without the list, stale files of the previous bundle stay; deleting by guess could hit container data.
            logger.LogWarning("Ignoring the unreadable manifest of stack {Stack}: {Reason}", stackName, ex.Message);
            return null;
        }
    }

    /// <summary>current → .prev (the old .prev is removed), then staged → current. Missing directories are skipped.</summary>
    private static void ReplaceDirectory(string staged, string current, bool keepPrevious = true)
    {
        var previous = current + ".prev";
        if (keepPrevious)
        {
            DeleteDirectory(previous);
            if (Directory.Exists(current))
            {
                Directory.Move(current, previous);
            }
        }
        else
        {
            DeleteDirectory(current);
        }

        if (Directory.Exists(staged))
        {
            Directory.Move(staged, current);
        }
    }

    // Recursive deletion removes symbolic links without following them.
    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <param name="Target">Path in the stack directory.</param>
    /// <param name="Staged">Where the file (or the link to a secret) is prepared before it is renamed to <see cref="Target"/>.</param>
    /// <param name="Secret">Where a secret's content ends up in the secrets directory; the link points here.</param>
    private sealed record PlannedFile(StackLayoutFile Source, string Target, string Staged, string Secret);
}
