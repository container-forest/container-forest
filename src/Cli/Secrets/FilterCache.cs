using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContainerForest.Cli.Secrets;

/// <summary>
/// Remembers, per file, the ciphertext that is in git and a MAC of the plain text it decrypts to. sops encrypts with a
/// fresh data key and nonces every time, so without this every <c>git status</c> would see each decrypted file as
/// changed; with it, unchanged plain text cleans to the very ciphertext it came from.
/// </summary>
/// <remarks>
/// Lives in <c>.git/forest</c>, never in the work tree. It holds no plain text: the MAC uses a random key of this clone
/// (<c>.git/forest/key</c>, owner-only), so the cache cannot be used to guess a short secret without the key.
/// </remarks>
internal sealed class FilterCache
{
    private const int KeyLength = 32;

    private readonly string directory;
    private readonly byte[] key;
    private readonly Dictionary<string, FilterCacheEntry> entries;
    private bool changed;

    private FilterCache(string directory, byte[] key, Dictionary<string, FilterCacheEntry> entries)
    {
        this.directory = directory;
        this.key = key;
        this.entries = entries;
    }

    private string CachePath => Path.Combine(directory, "cache.json");

    public static FilterCache Open(string gitDirectory)
    {
        var directory = Path.Combine(gitDirectory, "forest");
        CreatePrivateDirectory(directory);
        var key = LoadOrCreateKey(Path.Combine(directory, "key"));
        var entries = new Dictionary<string, FilterCacheEntry>(StringComparer.Ordinal);
        try
        {
            var stored = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(directory, "cache.json")), FilterCacheJsonContext.Default.DictionaryStringFilterCacheEntry);
            foreach (var (path, entry) in stored ?? [])
            {
                entries[path] = entry;
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or JsonException)
        {
            // No cache yet, or a damaged one: files are encrypted again, which only shows as a change.
        }

        return new FilterCache(directory, key, entries);
    }

    /// <summary>The ciphertext this plain text came from, when it has not changed since.</summary>
    public byte[]? Find(string path, byte[] plain) =>
        entries.TryGetValue(path, out var entry) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(entry.Mac), Mac(path, plain))
            ? Convert.FromBase64String(entry.Ciphertext)
            : null;

    public void Remember(string path, byte[] plain, byte[] ciphertext)
    {
        entries[path] = new FilterCacheEntry(Convert.ToHexStringLower(Mac(path, plain)), Convert.ToBase64String(ciphertext));
        changed = true;
    }

    /// <summary>Forgets the files, so they are encrypted afresh next time (after a change of recipients).</summary>
    public void Forget(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            changed |= entries.Remove(path);
        }
    }

    /// <summary>Writes the cache if it changed: a new file renamed over the old one.</summary>
    public void Save()
    {
        if (!changed)
        {
            return;
        }

        var temporary = CachePath + $".{Environment.ProcessId}.tmp";
        try
        {
            using (var stream = PrivateFile(temporary))
            {
                JsonSerializer.Serialize(stream, entries, FilterCacheJsonContext.Default.DictionaryStringFilterCacheEntry);
            }

            File.Move(temporary, CachePath, overwrite: true);
            changed = false;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // The path is part of the MAC: the same secret in two files must not reuse one file's ciphertext for the other.
    private byte[] Mac(string path, byte[] plain)
    {
        var name = Encoding.UTF8.GetBytes(path + "\0");
        var message = new byte[name.Length + plain.Length];
        name.CopyTo(message, 0);
        plain.CopyTo(message, name.Length);
        return HMACSHA256.HashData(key, message);
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path) && File.ReadAllBytes(path) is { Length: KeyLength } existing)
        {
            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(KeyLength);
        var temporary = path + $".{Environment.ProcessId}.tmp";
        using (var stream = PrivateFile(temporary))
        {
            stream.Write(key);
        }

        File.Move(temporary, path, overwrite: true);
        return key;
    }

    private static FileStream PrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

/// <param name="Mac">HMAC-SHA256 of the path and the plain text, hex.</param>
/// <param name="Ciphertext">The encrypted file as git has it, base64.</param>
internal sealed record FilterCacheEntry(string Mac, string Ciphertext);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, FilterCacheEntry>))]
internal sealed partial class FilterCacheJsonContext : JsonSerializerContext;
