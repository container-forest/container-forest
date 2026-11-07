using System.Formats.Tar;
using System.IO.Compression;
using ContainerForest.Aggregator.Application.Backups;
using ContainerForest.Aggregator.Infrastructure.Secrets;
using Microsoft.Data.Sqlite;

namespace ContainerForest.Aggregator.Infrastructure.Backups;

/// <summary>
/// Archives what the aggregator cannot do without: the database and the key ring. Git clones are left out, they can be
/// fetched again. The unencrypted files stay only in a private directory on the database's own volume, and only for the run.
/// </summary>
internal sealed class SqliteBackupArchiver(string connectionString, string keyDirectory, string agePath) : IBackupArchiver
{
    public const string DatabaseEntry = "aggregator.db";
    public const string KeyRingEntry = "data-protection-keys";
    private const string WorkDirectoryName = ".backup-tmp";
    private static readonly TimeSpan EncryptionTimeout = TimeSpan.FromMinutes(10);

    public async Task<BackupArchive> CreateAsync(string recipient, CancellationToken cancellationToken)
    {
        var databasePath = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrEmpty(databasePath) || databasePath == ":memory:")
        {
            throw new BackupFailedException("The database is not a file, so there is nothing to back up.");
        }

        var workDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, WorkDirectoryName);

        // Left by a run that crashed. Runs do not overlap (BackupGate), so nothing here is in use.
        if (Directory.Exists(workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
        }

        CreatePrivateDirectory(workDirectory);
        try
        {
            var snapshot = Path.Combine(workDirectory, DatabaseEntry);
            try
            {
                await SqliteSnapshot.WriteAsync(connectionString, snapshot, cancellationToken);
                if (!await SqliteSnapshot.IsIntactAsync(snapshot, cancellationToken))
                {
                    throw new BackupFailedException("The copy of the database did not pass the integrity check.");
                }
            }
            catch (SqliteException exception)
            {
                throw new BackupFailedException($"Could not copy the database: {exception.Message}", exception);
            }

            // After the database: every key its values were protected with is then in the ring. Keys are never removed from it.
            var keys = Directory.Exists(keyDirectory) ? Directory.GetFiles(keyDirectory) : [];
            if (keys.Length == 0)
            {
                throw new BackupFailedException("The key ring is empty: without it the backup could not decrypt anything.");
            }

            var plain = Path.Combine(workDirectory, "backup.tar.gz");
            await WriteTarAsync(plain, snapshot, keys, cancellationToken);

            var encrypted = plain + ".age";
            await EncryptAsync(recipient, plain, encrypted, cancellationToken);

            // The plain files are removed now: only the encrypted one stays while it uploads.
            File.Delete(snapshot);
            File.Delete(plain);
            return new BackupArchive(encrypted, new FileInfo(encrypted).Length, () => Directory.Delete(workDirectory, recursive: true));
        }
        catch
        {
            Directory.Delete(workDirectory, recursive: true);
            throw;
        }
    }

    private static async Task WriteTarAsync(string target, string snapshot, string[] keys, CancellationToken cancellationToken)
    {
        await using var file = File.Create(target);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        await using var tar = new TarWriter(gzip);
        await tar.WriteEntryAsync(snapshot, DatabaseEntry, cancellationToken);
        foreach (var key in keys)
        {
            await tar.WriteEntryAsync(key, $"{KeyRingEntry}/{Path.GetFileName(key)}", cancellationToken);
        }
    }

    private async Task EncryptAsync(string recipient, string source, string target, CancellationToken cancellationToken)
    {
        CliResult result;
        try
        {
            result = await CliProcess.RunAsync(
                agePath,
                ["--encrypt", "--recipient", recipient, "--output", target, source],
                new Dictionary<string, string>(),
                stdin: null,
                EncryptionTimeout,
                cancellationToken);
        }
        catch (CliUnavailableException exception)
        {
            throw new BackupFailedException("The age binary cannot be started.", exception);
        }

        if (result.ExitCode != 0)
        {
            throw new BackupFailedException($"age could not encrypt the archive: {result.Stderr.Trim()}");
        }
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
