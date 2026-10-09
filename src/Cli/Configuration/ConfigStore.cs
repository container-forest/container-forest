using System.Text.Json;

namespace ContainerForest.Cli.Configuration;

/// <summary>
/// Reads and writes <c>config.json</c> in <c>$XDG_CONFIG_HOME/container-forest</c> (<c>~/.config/container-forest</c>
/// without it, <c>%APPDATA%\container-forest</c> on Windows). The file holds API tokens: on Unix the directory is
/// created 0700 and the file 0600 from the start, and a write replaces the file atomically.
/// </summary>
public sealed class ConfigStore(string directory)
{
    public const string FileName = "config.json";

    public string Directory { get; } = directory;

    public string FilePath => Path.Combine(Directory, FileName);

    /// <summary>The store at the platform's place for user configuration.</summary>
    /// <param name="getVariable">Reads an environment variable; the process environment outside tests.</param>
    public static ConfigStore Default(Func<string, string?> getVariable)
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = getVariable("APPDATA") is { Length: > 0 } fromVariable
                ? fromVariable
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new ConfigStore(Path.Combine(appData, "container-forest"));
        }

        var configHome = getVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(getVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return new ConfigStore(Path.Combine(configHome, "container-forest"));
    }

    /// <summary>The configuration; an empty one when there is no file yet.</summary>
    /// <exception cref="CliException">The file exists but is not a valid configuration.</exception>
    public CliConfig Load()
    {
        if (!File.Exists(FilePath))
        {
            return new CliConfig();
        }

        try
        {
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize(stream, CliConfigJsonContext.Default.CliConfig) ?? new CliConfig();
        }
        catch (JsonException exception)
        {
            throw new CliException($"{FilePath} is not a valid forest configuration: {exception.Message}");
        }
    }

    public void Save(CliConfig config)
    {
        CreateDirectory();

        // A new file, private from the first byte, then renamed over the old one: a crash never leaves half a file,
        // and the token is never readable by others, not even for a moment.
        var temporary = FilePath + $".{Environment.ProcessId}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, config, CliConfigJsonContext.Default.CliConfig);
            }

            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private void CreateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(Directory);
            return;
        }

        System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
