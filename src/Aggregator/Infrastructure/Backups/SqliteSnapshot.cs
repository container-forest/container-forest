using Microsoft.Data.Sqlite;

namespace ContainerForest.Aggregator.Infrastructure.Backups;

/// <summary>A consistent copy of a live SQLite database in one file, including what is still in its WAL.</summary>
internal static class SqliteSnapshot
{
    /// <summary>Replaces <paramref name="targetPath"/>. VACUUM INTO only reads the source, so writers are not blocked.</summary>
    public static async Task WriteAsync(string connectionString, string targetPath, CancellationToken cancellationToken)
    {
        File.Delete(targetPath);
        var builder = new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target;";
        command.Parameters.AddWithValue("$target", targetPath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Whether the file is a sound database.</summary>
    public static async Task<bool> IsIntactAsync(string path, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return await command.ExecuteScalarAsync(cancellationToken) is "ok";
    }
}
