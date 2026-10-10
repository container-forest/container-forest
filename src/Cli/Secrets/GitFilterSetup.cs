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
