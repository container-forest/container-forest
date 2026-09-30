using System.Text.Json;

namespace ContainerForest.Agent.Stacks;

/// <summary>
/// The files the agent wrote into a stack directory at the last deployment. Everything else in the directory,
/// such as data of bind mounts, belongs to the containers and is never touched.
/// </summary>
/// <remarks>
/// Kept next to the stack directory (&lt;StacksDirectory&gt;/&lt;stack&gt;.manifest.json), not inside it:
/// a container that bind-mounts its stack directory could otherwise rewrite the list of files the agent deletes.
/// </remarks>
public sealed record StackManifest(IReadOnlyList<StackManifestEntry> Files)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static StackManifest Of(StackLayout layout) =>
        new(layout.Files.Select(f => new StackManifestEntry(f.Path, f.Secret)).ToList());

    /// <summary>Entries of both manifests; while files are being replaced, either version's files may be present.</summary>
    public StackManifest Union(StackManifest other, StringComparer comparer) =>
        new(Files.Concat(other.Files).DistinctBy(f => f.Path, comparer).ToList());

    /// <returns>Null when there is no manifest, i.e. nothing was deployed yet.</returns>
    /// <exception cref="JsonException">The file is not a manifest.</exception>
    public static async Task<StackManifest?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var file = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StackManifest>(file, Json, cancellationToken)
            ?? throw new JsonException("The manifest is empty.");
    }

    /// <summary>Writes atomically: a crash leaves either the old or the new manifest.</summary>
    public async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write))
        {
            await JsonSerializer.SerializeAsync(file, this, Json, cancellationToken);
            await file.FlushAsync(cancellationToken);
            file.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }
}

/// <param name="Path">Relative to the stack directory, as in <see cref="StackLayoutFile.Path"/>.</param>
/// <param name="Secret">The stack directory holds a link to the secrets directory at this path.</param>
public sealed record StackManifestEntry(string Path, bool Secret);
