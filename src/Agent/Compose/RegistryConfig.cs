using System.Text;
using System.Text.Json;
using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Configuration;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Compose;

/// <summary>
/// Writes registry credentials of one deployment into a temporary docker config directory for DOCKER_CONFIG.
/// On Linux it lives on the secrets tmpfs, so the passwords never reach a disk, and it is removed after compose.
/// </summary>
public sealed class RegistryConfigWriter(IOptions<AgentOptions> options, ILogger<RegistryConfigWriter> logger)
{
    /// <summary>The variable the docker CLI reads its config directory from.</summary>
    public const string DockerConfigVariable = "DOCKER_CONFIG";

    // Starts with '.', which a stack name cannot, so it never collides with a stack's secrets directory.
    private const string ParentName = ".docker-config";

    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <returns>The config directory, or null when there are no credentials and the CLI's own config applies.</returns>
    /// <exception cref="InvalidCommandArgumentException">A server or user name is not usable.</exception>
    public async Task<TemporaryRegistryConfig?> WriteAsync(IReadOnlyList<RegistryAuth> auths, CancellationToken cancellationToken)
    {
        if (auths.Count == 0)
        {
            return null;
        }

        Validate(auths);

        var parent = ParentDirectory();
        var directory = Path.Combine(parent, Guid.CreateVersion7().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            logger.LogWarning("Registry credentials are written to {Directory} on disk: Windows has no secrets tmpfs", directory);
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(parent, OwnerOnlyDirectory);
            Directory.CreateDirectory(directory, OwnerOnlyDirectory);
        }

        var config = new TemporaryRegistryConfig(directory, logger);
        try
        {
            await WriteConfigAsync(Path.Combine(directory, "config.json"), auths, cancellationToken);
        }
        catch
        {
            config.Dispose();
            throw;
        }

        return config;
    }

    private string ParentDirectory() =>
        OperatingSystem.IsWindows()
            ? Path.Combine(options.Value.DataDirectory, ParentName)
            : Path.Combine(options.Value.SecretsDirectory, ParentName);

    /// <exception cref="InvalidCommandArgumentException">A server or user name is not usable.</exception>
    public static void Validate(IReadOnlyList<RegistryAuth> auths)
    {
        var servers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var auth in auths)
        {
            if (string.IsNullOrEmpty(auth.Server) || auth.Server.Length > 255 || auth.Server.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                throw new InvalidCommandArgumentException("A registry server name is empty or has spaces or control characters.");
            }

            if (!servers.Add(auth.Server))
            {
                throw new InvalidCommandArgumentException($"Registry '{auth.Server}' has more than one set of credentials.");
            }

            // docker stores "user:password" in base64 and splits it at the first ':'.
            if (string.IsNullOrEmpty(auth.Username) || auth.Username.Contains(':') || auth.Username.Any(char.IsControl))
            {
                throw new InvalidCommandArgumentException($"The user name for registry '{auth.Server}' is empty or contains ':'.");
            }
        }
    }

    private static async Task WriteConfigAsync(string path, IReadOnlyList<RegistryAuth> auths, CancellationToken cancellationToken)
    {
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var file = new FileStream(path, fileOptions);
        await using var json = new Utf8JsonWriter(file);

        // {"auths": {"<server>": {"auth": base64("user:password")}}}: the format `docker login` writes without a credential store.
        json.WriteStartObject();
        json.WriteStartObject("auths");
        foreach (var auth in auths)
        {
            json.WriteStartObject(auth.Server);
            json.WriteString("auth", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}")));
            json.WriteEndObject();
        }

        json.WriteEndObject();
        json.WriteEndObject();
        await json.FlushAsync(cancellationToken);
    }
}

/// <summary>A docker config directory with registry credentials; disposing it removes the directory.</summary>
public sealed class TemporaryRegistryConfig(string directory, ILogger logger) : IDisposable
{
    public string Directory { get; } = directory;

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove registry credentials in {Directory}: {Reason}", Directory, ex.Message);
        }
    }
}
