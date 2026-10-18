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

    /// <summary>Removes a key from the repository's own config; a key that is not there is fine.</summary>
    public void UnsetConfig(string key) => RunRaw(Root, ["config", "--local", "--unset-all", key], null);

    /// <summary>Removes a whole section (e.g. filter.forest) from the repository's own config, if it is there.</summary>
    public void RemoveConfigSection(string section) => RunRaw(Root, ["config", "--local", "--remove-section", section], null);

    /// <summary>Tracked files, relative to the root.</summary>
    public IReadOnlyList<string> TrackedFiles() => Split0(Run(Root, ["ls-files", "-z"]));

    /// <summary>Files added, copied, modified or renamed in the index, relative to the root.</summary>
    public IReadOnlyList<string> StagedFiles() =>
        Split0(Run(Root, ["diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z"]));

    /// <summary>Of the given files, those .gitattributes puts under the forest filter (as the index has the attributes).</summary>
    public IReadOnlyList<string> Filtered(IReadOnlyList<string> paths) =>
        Attribute(paths, "filter").Where(a => a.Value == FilterDriver).Select(a => a.Path).ToList();

    /// <summary>
    /// The value .gitattributes gives an attribute for each file: "set", "unset", "unspecified" or the value, as
    /// git check-attr prints it.
    /// </summary>
    public IReadOnlyList<(string Path, string Value)> Attribute(IReadOnlyList<string> paths, string attribute)
    {
        var values = new List<(string, string)>();
        foreach (var chunk in paths.Chunk(500))
        {
            // -z output: path NUL attribute NUL value NUL, for each path.
            var fields = Split0(Run(Root, ["check-attr", "-z", attribute, "--", .. chunk]));
            for (var i = 0; i + 2 < fields.Count; i += 3)
            {
                values.Add((fields[i], fields[i + 2]));
            }
        }

        return values;
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
    /// <returns>What git and the filter said on stderr, line by line: why a file stays encrypted, for one.</returns>
    public IReadOnlyList<string> CheckOutAgain(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            File.Delete(Path.Combine(Root, path));
        }

        var messages = new List<string>();
        foreach (var chunk in paths.Chunk(500))
        {
            var result = RunRaw(Root, ["checkout", "--", .. chunk], null);
            if (result.ExitCode != 0)
            {
                throw Failed("checkout", result.ExitCode, result.Stderr);
            }

            messages.AddRange(result.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return messages;
    }

    /// <summary>Stages the files as they are in the work tree.</summary>
    public void Stage(IReadOnlyList<string> paths)
    {
        foreach (var chunk in paths.Chunk(500))
        {
            Run(Root, ["add", "--", .. chunk]);
        }
    }

    /// <summary>
    /// Stages exactly this content for a tracked file, past the filter and line ending conversion, and leaves the work
    /// tree alone: its uncommitted changes stay unstaged.
    /// </summary>
    public void StageContent(string path, byte[] content)
    {
        // ls-files -s: "<mode> <object> <stage>\t<path>"
        var mode = Text(Run(Root, ["ls-files", "-s", "--", path])).Split(' ', 2)[0];
        var stored = RunRaw(Root, ["hash-object", "-w", "--no-filters", "--stdin"], content);
        if (stored.ExitCode != 0)
        {
            throw Failed("hash-object", stored.ExitCode, stored.Stderr);
        }

        Run(Root, ["update-index", "--cacheinfo", $"{mode},{Text(stored.Stdout).Trim()},{path}"]);
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
        return result.ExitCode == 0 ? result.Stdout : throw Failed(arguments[0], result.ExitCode, result.Stderr);
    }

    private static CliException Failed(string command, int exitCode, string stderr)
    {
        var reason = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? $"exit code {exitCode}";
        return new CliException($"git {command} failed: {reason}");
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
