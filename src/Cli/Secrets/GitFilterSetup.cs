using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Secrets;

/// <summary>
/// Turns the forest filter on in a clone (<c>forest git setup</c>): the filter and diff drivers in the repository's
/// git config, a pre-commit hook that refuses plain text, and a fresh checkout of the files under the filter so they
/// appear decrypted.
/// </summary>
internal static class GitFilterSetup
{
    public const string ContextKey = "forest.context";
    public const string LocalSopsKey = "forest.localSops";
    public const string SopsPathKey = "forest.sopsPath";

    /// <summary>Marks the hook as ours, so setup may rewrite it and leaves anyone else's alone.</summary>
    public const string HookMarker = "# Installed by 'forest git setup'";

    /// <returns>How many files under the filter are now decrypted, and how many there are.</returns>
    public static (int Decrypted, int Total) Apply(GitWorkTree tree, string? contextName, string executable, CliOutput output)
    {
        tree.SetConfig("filter.forest.process", $"{executable} git filter-process");
        tree.SetConfig("filter.forest.required", "true");
        tree.SetConfig("diff.forest.textconv", $"{executable} git textconv");
        tree.SetConfig("diff.forest.cachetextconv", "false");
        if (contextName is not null)
        {
            tree.SetConfig(ContextKey, contextName);
        }

        InstallHook(tree, executable, output);
        return Refresh(tree);
    }

    /// <summary>
    /// Turns the filter off in a clone (<c>forest git teardown</c>): what <see cref="Apply"/> wrote goes, the files it
    /// decrypted are checked out again as the ciphertext git has, and the filter cache of the clone is deleted.
    /// .gitattributes and .sops.yaml stay: they belong to the repository, not to the clone.
    /// </summary>
    /// <returns>How many files were encrypted again in the work tree.</returns>
    /// <exception cref="CliException">A decrypted file has changes that are not committed; they would be lost.</exception>
    public static int Remove(GitWorkTree tree, CliOutput output)
    {
        var filtered = tree.Filtered(tree.TrackedFiles());
        var decrypted = filtered.Where(p => tree.WorkTreeFile(p) is { } content && !SopsFiles.IsEncrypted(p, content)).ToList();
        if (tree.GetConfig("filter.forest.process") is not null)
        {
            // Checked while the filter still works: a changed plain text would be overwritten by the ciphertext.
            var changed = decrypted.Where(p => !tree.IsUnmodified(p)).ToList();
            if (changed.Count > 0)
            {
                throw new CliException(
                    $"Changed and not committed: {string.Join(", ", changed)}. Commit or discard the changes first.", ExitCodes.Failed);
            }
        }

        tree.RemoveConfigSection("filter.forest");
        tree.RemoveConfigSection("diff.forest");
        foreach (var key in new[] { ContextKey, LocalSopsKey, SopsPathKey })
        {
            tree.UnsetConfig(key);
        }

        RemoveHook(tree, output);
        tree.CheckOutAgain(decrypted);
        var cache = Path.Combine(tree.GitDirectory, "forest");
        if (Directory.Exists(cache))
        {
            Directory.Delete(cache, recursive: true);
        }

        return decrypted.Count;
    }

    /// <summary>
    /// How git should start this CLI: the absolute path of this binary when it is forest itself (not dotnet running
    /// it), quoted for the shell git starts filters with.
    /// </summary>
    public static string Executable()
    {
        var path = Environment.ProcessPath;
        var name = Path.GetFileNameWithoutExtension(path);
        return path is not null && name == "forest" ? $"'{path.Replace("'", "'\\''", StringComparison.Ordinal)}'" : "forest";
    }

    /// <summary>Checks out again the files under the filter that are still encrypted and unchanged, through smudge.</summary>
    public static (int Decrypted, int Total) Refresh(GitWorkTree tree)
    {
        var filtered = tree.Filtered(tree.TrackedFiles());
        var stale = filtered
            .Where(p => tree.WorkTreeFile(p) is { } content && SopsFiles.IsEncrypted(p, content) && tree.IsUnmodified(p))
            .ToList();
        if (stale.Count > 0)
        {
            tree.CheckOutAgain(stale);
        }

        var decrypted = filtered.Count(p => tree.WorkTreeFile(p) is { } content && !SopsFiles.IsEncrypted(p, content));
        return (decrypted, filtered.Count);
    }

    private static void RemoveHook(GitWorkTree tree, CliOutput output)
    {
        var hook = tree.GitPath("hooks/pre-commit");
        if (!File.Exists(hook))
        {
            return;
        }

        var text = File.ReadAllText(hook);
        if (text.Contains(HookMarker, StringComparison.Ordinal))
        {
            File.Delete(hook);
        }
        else if (text.Contains("secrets check", StringComparison.Ordinal))
        {
            output.Note($"{hook} is not the one 'forest git setup' wrote: remove its 'secrets check' line yourself.");
        }
    }

    private static void InstallHook(GitWorkTree tree, string executable, CliOutput output)
    {
        var hook = tree.GitPath("hooks/pre-commit");
        if (File.Exists(hook) && !File.ReadAllText(hook).Contains(HookMarker, StringComparison.Ordinal))
        {
            output.Note($"{hook} exists: add '{executable} secrets check --staged' to it, so plain text is never committed.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, $"""
            #!/bin/sh
            {HookMarker}: refuses to commit files under filter=forest in plain text (ADR-0010).
            exec {executable} secrets check --staged

            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, File.GetUnixFileMode(hook) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }
}
