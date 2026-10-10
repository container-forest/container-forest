using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ContainerForest.Cli.Secrets;

/// <summary>
/// The git repository the CLI works in, through the git binary: the secrets commands need what git itself knows
/// (attributes, the index, configuration), and git runs the filter anyway.
/// </summary>
internal sealed class GitWorkTree
{
    /// <summary>The attribute value that puts a file under the forest filter.</summary>
    public const string FilterDriver = "forest";

    private GitWorkTree(string root, string gitDirectory)
    {
        Root = root;
        GitDirectory = gitDirectory;
    }

    /// <summary>The top of the work tree; repository paths are relative to it.</summary>
    public string Root { get; }

    /// <summary>The .git directory (or the worktree's own), absolute.</summary>
    public string GitDirectory { get; }

    /// <exception cref="CliException">The directory is not in a git work tree, or git is not installed.</exception>
    public static GitWorkTree Open(string directory)
    {
        var lines = Text(Run(directory, ["rev-parse", "--show-toplevel", "--absolute-git-dir"])).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 2
            ? new GitWorkTree(lines[0], lines[1])
            : throw new CliException("Run this inside a git work tree.", ExitCodes.Usage);
    }

    public string? GetConfig(string key)
    {
        var result = RunRaw(Root, ["config", "--get", key], null);
        return result.ExitCode == 0 ? Text(result.Stdout).Trim() : null;
    }

    public void SetConfig(string key, string value) => Run(Root, ["config", "--local", key, value]);

    /// <summary>Tracked files, relative to the root.</summary>
    public IReadOnlyList<string> TrackedFiles() => Split0(Run(Root, ["ls-files", "-z"]));

    /// <summary>Files added, copied, modified or renamed in the index, relative to the root.</summary>
    public IReadOnlyList<string> StagedFiles() =>
        Split0(Run(Root, ["diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z"]));

    /// <summary>Of the given files, those .gitattributes puts under the forest filter (as the index has the attributes).</summary>
    public IReadOnlyList<string> Filtered(IReadOnlyList<string> paths)
    {
        var filtered = new List<string>();
        foreach (var chunk in paths.Chunk(500))
        {
            // -z output: path NUL attribute NUL value NUL, for each path.
            var fields = Split0(Run(Root, ["check-attr", "-z", "filter", "--", .. chunk]));
            for (var i = 0; i + 2 < fields.Count; i += 3)
            {
                if (fields[i + 2] == FilterDriver)
                {
                    filtered.Add(fields[i]);
                }
            }
        }

        return filtered;
    }

    /// <summary>The file as the index holds it: what the next commit will contain.</summary>
    public byte[] IndexBlob(string path) => Run(Root, ["cat-file", "blob", $":{path}"]);

    /// <summary>The file in the work tree; null when it does not exist.</summary>
    public byte[]? WorkTreeFile(string path)
    {
        var full = Path.Combine(Root, path);
        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }

    /// <summary>True when the work tree file has no changes git would show; it runs the clean filter to tell.</summary>
    public bool IsUnmodified(string path) => RunRaw(Root, ["diff", "--quiet", "--", path], null).ExitCode == 0;

    /// <summary>Writes the files again from the index, through the smudge filter.</summary>
    public void CheckOutAgain(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            File.Delete(Path.Combine(Root, path));
        }

        foreach (var chunk in paths.Chunk(500))
        {
            Run(Root, ["checkout", "--", .. chunk]);
        }
    }

    /// <summary>Stages the files again through the clean filter, even when git thinks they did not change.</summary>
    public void Renormalize(IReadOnlyList<string> paths)
    {
        foreach (var chunk in paths.Chunk(500))
        {
            Run(Root, ["add", "--renormalize", "--", .. chunk]);
        }
    }

    /// <summary>A path inside the .git directory that git itself would use, e.g. hooks/pre-commit (honours core.hooksPath).</summary>
    public string GitPath(string relative)
    {
        var path = Text(Run(Root, ["rev-parse", "--git-path", relative])).Trim();
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root, path));
    }

    private static byte[] Run(string directory, IReadOnlyList<string> arguments)
    {
        var result = RunRaw(directory, arguments, null);
        if (result.ExitCode != 0)
        {
            var reason = result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? $"exit code {result.ExitCode}";
            throw new CliException($"git {arguments[0]} failed: {reason}");
        }

        return result.Stdout;
    }

    private static (int ExitCode, byte[] Stdout, string Stderr) RunRaw(string directory, IReadOnlyList<string> arguments, byte[]? stdin)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new CliException("git could not be started.");
        }
        catch (Win32Exception exception)
        {
            throw new CliException($"git could not be started: {exception.Message}");
        }

        using (process)
        {
            var stderr = process.StandardError.ReadToEndAsync();
            using var stdout = new MemoryStream();
            if (stdin is not null)
            {
                process.StandardInput.BaseStream.Write(stdin);
            }

            process.StandardInput.Close();
            process.StandardOutput.BaseStream.CopyTo(stdout);
            process.WaitForExit();
            return (process.ExitCode, stdout.ToArray(), stderr.GetAwaiter().GetResult());
        }
    }

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static List<string> Split0(byte[] bytes) =>
        Text(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
}
