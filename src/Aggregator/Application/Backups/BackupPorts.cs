using ContainerForest.Aggregator.Domain.Backups;

namespace ContainerForest.Aggregator.Application.Backups;

/// <summary>A backup step failed for a reason the administrator can act on. The message holds no secret and is shown to them.</summary>
public sealed class BackupFailedException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>An encrypted archive in a temporary place; disposing removes it.</summary>
public sealed class BackupArchive(string path, long sizeBytes, Action remove) : IDisposable
{
    public string Path { get; } = path;

    public long SizeBytes { get; } = sizeBytes;

    public void Dispose() => remove();
}

public interface IBackupArchiver
{
    /// <summary>Makes an archive of a consistent copy of the database and the key ring, encrypted for <paramref name="recipient"/> alone.</summary>
    /// <exception cref="BackupFailedException"/>
    Task<BackupArchive> CreateAsync(string recipient, CancellationToken cancellationToken);
}

public interface IBackupStorage
{
    /// <exception cref="BackupFailedException"/>
    Task UploadAsync(BackupDestination destination, string filePath, string objectKey, CancellationToken cancellationToken);
}

public interface IBackupRunRepository
{
    void Add(BackupRun run);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<BackupRun>> ListRecentAsync(int limit, CancellationToken cancellationToken);

    Task<BackupRun?> FindLastSucceededAsync(CancellationToken cancellationToken);

    Task RemoveOlderRunsAsync(int keep, CancellationToken cancellationToken);
}

/// <summary>Lets one backup run at a time, whoever started it.</summary>
public sealed class BackupGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public bool IsBusy => gate.CurrentCount == 0;

    public bool TryEnter() => gate.Wait(0);

    public void Exit() => gate.Release();
}
