using System.Text.Json;
using ContainerForest.Agent.Configuration;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Enrollment;

/// <summary>Keeps the agent credentials in a file only the agent's user can read.</summary>
public sealed class CredentialStore(IOptions<AgentOptions> options)
{
    public const string FileName = "agent-credentials.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string FilePath => Path.Combine(options.Value.DataDirectory, FileName);

    /// <summary>Returns null when the agent has not enrolled yet.</summary>
    /// <exception cref="FatalAgentException">The file exists but cannot be used.</exception>
    public async Task<AgentCredentials?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        AgentCredentials? credentials;
        try
        {
            await using var file = File.OpenRead(FilePath);
            credentials = await JsonSerializer.DeserializeAsync<AgentCredentials>(file, Json, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new FatalAgentException($"Cannot read agent credentials from {FilePath}: {ex.Message}", ex);
        }

        if (credentials is null
            || string.IsNullOrWhiteSpace(credentials.AgentId)
            || string.IsNullOrWhiteSpace(credentials.AgentSecret))
        {
            throw new FatalAgentException(
                $"Agent credentials in {FilePath} are incomplete. Delete the file and enroll again with a new token.");
        }

        return credentials;
    }

    /// <summary>Writes the credentials atomically: a crash never leaves a half-written file.</summary>
    public async Task SaveAsync(AgentCredentials credentials, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        var temporary = FilePath + ".tmp";

        // UnixCreateMode applies only to a newly created file, so a leftover from a crash is removed first.
        File.Delete(temporary);
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            // 0600 from the moment the file exists; on Windows the user profile already protects it.
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (var file = new FileStream(temporary, fileOptions))
        {
            await JsonSerializer.SerializeAsync(file, credentials, Json, cancellationToken);
            await file.FlushAsync(cancellationToken);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, FilePath, overwrite: true);
    }
}
