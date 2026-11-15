using System.Globalization;
using ContainerForest.Aggregator.Application.Metrics;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.Data.Sqlite;

namespace ContainerForest.Aggregator.Infrastructure.Metrics;

/// <summary>
/// <see cref="IMetricsStore"/> in its own SQLite file (ConnectionStrings:Metrics, /data/metrics.db in the image), with
/// plain SQL: time series need nothing from EF but would pay for its change tracking on every write. The schema is
/// created on first use and versioned with PRAGMA user_version; tables are WITHOUT ROWID with (environment, series,
/// start) keys, so a range read is one index scan.
/// </summary>
internal sealed class SqliteMetricsStore : IMetricsStore
{
    private const int SchemaVersion = 1;

    private const string ContainerColumns =
        "environment_id, container, start, stack, service, cpu_avg, cpu_max, cpu_limit, throttled_avg, " +
        "memory_avg, memory_max, memory_limit, cache_avg, rx_avg, tx_avg, read_avg, write_avg, pids_max";

    private const string HostColumns =
        "environment_id, start, cpu_count, cpu_user, cpu_system, cpu_iowait, cpu_steal, cpu_busy_max, load1, " +
        "memory_used_avg, memory_used_max, memory_total, swap_used, swap_total, disk_read, disk_write, network_rx, network_tx";

    // How a bucket of shorter periods becomes one point: averages of averages, maxima of maxima, the last-known limits.
    private const string ContainerAggregates =
        "MAX(stack), MAX(service), AVG(cpu_avg), MAX(cpu_max), MAX(cpu_limit), AVG(throttled_avg), AVG(memory_avg), MAX(memory_max), " +
        "MAX(memory_limit), AVG(cache_avg), AVG(rx_avg), AVG(tx_avg), AVG(read_avg), AVG(write_avg), MAX(pids_max)";

    private const string HostAggregates =
        "MAX(cpu_count), AVG(cpu_user), AVG(cpu_system), AVG(cpu_iowait), AVG(cpu_steal), MAX(cpu_busy_max), AVG(load1), " +
        "AVG(memory_used_avg), MAX(memory_used_max), MAX(memory_total), AVG(swap_used), MAX(swap_total), AVG(disk_read), " +
        "AVG(disk_write), AVG(network_rx), AVG(network_tx)";

    private readonly string connectionString;
    private readonly SemaphoreSlim schemaLock = new(1, 1);
    private bool schemaReady;

    public SqliteMetricsStore(string connectionString)
    {
        this.connectionString = new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString;
    }

    public async Task AppendAsync(IReadOnlyCollection<MinuteBatch> batches, CancellationToken cancellationToken)
    {
        if (batches.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var insert = Command(connection, $"INSERT OR REPLACE INTO container_minute ({ContainerColumns}) VALUES ({Parameters(18)})"))
        {
            insert.Transaction = transaction;
            foreach (var p in batches.SelectMany(b => b.Containers))
            {
                Bind(insert, p.EnvironmentId.ToString(), p.Container, p.Start, p.Stack, p.Service, p.CpuAvg, p.CpuMax, p.CpuLimit,
                    p.ThrottledAvg, p.MemoryAvg, p.MemoryMax, p.MemoryLimit, p.CacheAvg, p.RxAvg, p.TxAvg, p.ReadAvg, p.WriteAvg, p.PidsMax);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var insert = Command(connection, $"INSERT OR REPLACE INTO host_minute ({HostColumns}) VALUES ({Parameters(18)})"))
        {
            insert.Transaction = transaction;
            foreach (var h in batches.Select(b => b.Host).OfType<HostPoint>())
            {
                Bind(insert, h.EnvironmentId.ToString(), h.Start, h.CpuCount, h.CpuUser, h.CpuSystem, h.CpuIowait, h.CpuSteal, h.CpuBusyMax,
                    h.Load1, h.MemoryUsedAvg, h.MemoryUsedMax, h.MemoryTotal, h.SwapUsed, h.SwapTotal, h.DiskRead, h.DiskWrite, h.NetworkRx, h.NetworkTx);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var insert = Command(connection, $"INSERT OR REPLACE INTO filesystem_minute (environment_id, mount_point, start, used, total) VALUES ({Parameters(5)})"))
        {
            insert.Transaction = transaction;
            foreach (var f in batches.SelectMany(b => b.Filesystems))
            {
                Bind(insert, f.EnvironmentId.ToString(), f.MountPoint, f.Start, f.Used, f.Total);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ContainerPoint>> QueryContainersAsync(
        EnvironmentId environmentId, MetricsResolution resolution, ContainerFilter filter, long from, long to, long stepSeconds,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var where = "environment_id = $p0 AND start >= $p1 AND start < $p2"
            + (filter.Container is not null ? " AND container = $p4" : "")
            + (filter.Stack is not null ? " AND stack = $p5" : "");
        await using var query = Command(
            connection,
            $"SELECT container, (start / $p3) * $p3 AS bucket, {ContainerAggregates} FROM container_{Table(resolution)} " +
            $"WHERE {where} GROUP BY container, bucket ORDER BY container, bucket");
        Bind(query, environmentId.ToString(), from, to, Math.Max(1, stepSeconds), filter.Container ?? "", filter.Stack ?? "");

        var result = new List<ContainerPoint>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ContainerPoint(
                environmentId, reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8),
                reader.GetDouble(9), reader.GetDouble(10), reader.GetDouble(11), reader.GetDouble(12), reader.GetDouble(13),
                reader.GetDouble(14), reader.GetDouble(15), reader.GetDouble(16)));
        }

        return result;
    }

    public async Task<IReadOnlyList<HostPoint>> QueryHostAsync(
        EnvironmentId environmentId, MetricsResolution resolution, long from, long to, long stepSeconds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var query = Command(
            connection,
            $"SELECT (start / $p3) * $p3 AS bucket, {HostAggregates} FROM host_{Table(resolution)} " +
            "WHERE environment_id = $p0 AND start >= $p1 AND start < $p2 GROUP BY bucket ORDER BY bucket");
        Bind(query, environmentId.ToString(), from, to, Math.Max(1, stepSeconds));

        var result = new List<HostPoint>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new HostPoint(
                environmentId, reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4),
                reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8), reader.GetDouble(9),
                reader.GetDouble(10), reader.GetDouble(11), reader.GetDouble(12), reader.GetDouble(13), reader.GetDouble(14),
                reader.GetDouble(15), reader.GetDouble(16)));
        }

        return result;
    }

    public async Task<IReadOnlyList<FilesystemPoint>> QueryFilesystemsAsync(
        EnvironmentId environmentId, MetricsResolution resolution, long from, long to, long stepSeconds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var query = Command(
            connection,
            $"SELECT mount_point, (start / $p3) * $p3 AS bucket, MAX(used), MAX(total) FROM filesystem_{Table(resolution)} " +
            "WHERE environment_id = $p0 AND start >= $p1 AND start < $p2 GROUP BY mount_point, bucket ORDER BY mount_point, bucket");
        Bind(query, environmentId.ToString(), from, to, Math.Max(1, stepSeconds));

        var result = new List<FilesystemPoint>();
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FilesystemPoint(environmentId, reader.GetString(0), reader.GetInt64(1), reader.GetDouble(2), reader.GetDouble(3)));
        }

        return result;
    }

    public async Task RollUpHoursAsync(long from, long to, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        string[] statements =
        [
            $"INSERT OR REPLACE INTO container_hour ({ContainerColumns}) SELECT environment_id, container, start / 3600 * 3600 AS hour, " +
            $"{ContainerAggregates} FROM container_minute WHERE start >= $p0 AND start < $p1 GROUP BY environment_id, container, hour",
            $"INSERT OR REPLACE INTO host_hour ({HostColumns}) SELECT environment_id, start / 3600 * 3600 AS hour, " +
            $"{HostAggregates} FROM host_minute WHERE start >= $p0 AND start < $p1 GROUP BY environment_id, hour",
            "INSERT OR REPLACE INTO filesystem_hour (environment_id, mount_point, start, used, total) SELECT environment_id, mount_point, " +
            "start / 3600 * 3600 AS hour, MAX(used), MAX(total) FROM filesystem_minute WHERE start >= $p0 AND start < $p1 " +
            "GROUP BY environment_id, mount_point, hour",
        ];
        foreach (var sql in statements)
        {
            await using var command = Command(connection, sql);
            command.Transaction = transaction;
            Bind(command, from, to);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> RemoveExpiredAsync(
        long minutesBefore, long hoursBefore, IReadOnlyCollection<EnvironmentId> environments, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        // Ids are Guids formatted by us, so they can go into the statement as literals.
        var known = environments.Count > 0 ? string.Join(", ", environments.Select(e => $"'{e.Value:D}'")) : "''";
        var removed = 0;
        foreach (var series in (string[])["container", "host", "filesystem"])
        {
            foreach (var (table, before) in new[] { ("minute", minutesBefore), ("hour", hoursBefore) })
            {
                await using var command = Command(connection, $"DELETE FROM {series}_{table} WHERE start < $p0 OR environment_id NOT IN ({known})");
                Bind(command, before);
                removed += await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        return removed;
    }

    private static string Table(MetricsResolution resolution) => resolution switch
    {
        MetricsResolution.Minute => "minute",
        MetricsResolution.Hour => "hour",
        _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Raw samples are not stored."),
    };

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        if (!schemaReady)
        {
            await schemaLock.WaitAsync(cancellationToken);
            try
            {
                if (!schemaReady)
                {
                    await CreateSchemaAsync(connection, cancellationToken);
                    schemaReady = true;
                }
            }
            finally
            {
                schemaLock.Release();
            }
        }

        return connection;
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var version = Command(connection, "PRAGMA user_version");
        if (Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= SchemaVersion)
        {
            return;
        }

        var tables = new List<string> { "PRAGMA journal_mode = WAL" };
        foreach (var table in (string[])["minute", "hour"])
        {
            tables.Add(
                $"CREATE TABLE IF NOT EXISTS container_{table} (environment_id TEXT NOT NULL, container TEXT NOT NULL, start INTEGER NOT NULL, " +
                "stack TEXT NOT NULL, service TEXT NOT NULL, cpu_avg REAL NOT NULL, cpu_max REAL NOT NULL, cpu_limit REAL NOT NULL, " +
                "throttled_avg REAL NOT NULL, memory_avg REAL NOT NULL, memory_max REAL NOT NULL, memory_limit REAL NOT NULL, " +
                "cache_avg REAL NOT NULL, rx_avg REAL NOT NULL, tx_avg REAL NOT NULL, read_avg REAL NOT NULL, write_avg REAL NOT NULL, " +
                "pids_max REAL NOT NULL, PRIMARY KEY (environment_id, container, start)) WITHOUT ROWID");
            tables.Add($"CREATE INDEX IF NOT EXISTS container_{table}_stack ON container_{table} (environment_id, stack, start)");
            tables.Add(
                $"CREATE TABLE IF NOT EXISTS host_{table} (environment_id TEXT NOT NULL, start INTEGER NOT NULL, cpu_count REAL NOT NULL, " +
                "cpu_user REAL NOT NULL, cpu_system REAL NOT NULL, cpu_iowait REAL NOT NULL, cpu_steal REAL NOT NULL, " +
                "cpu_busy_max REAL NOT NULL, load1 REAL NOT NULL, memory_used_avg REAL NOT NULL, memory_used_max REAL NOT NULL, " +
                "memory_total REAL NOT NULL, swap_used REAL NOT NULL, swap_total REAL NOT NULL, disk_read REAL NOT NULL, " +
                "disk_write REAL NOT NULL, network_rx REAL NOT NULL, network_tx REAL NOT NULL, PRIMARY KEY (environment_id, start)) WITHOUT ROWID");
            tables.Add(
                $"CREATE TABLE IF NOT EXISTS filesystem_{table} (environment_id TEXT NOT NULL, mount_point TEXT NOT NULL, " +
                "start INTEGER NOT NULL, used REAL NOT NULL, total REAL NOT NULL, PRIMARY KEY (environment_id, mount_point, start)) WITHOUT ROWID");
        }

        tables.Add($"PRAGMA user_version = {SchemaVersion}");
        foreach (var sql in tables)
        {
            await using var command = Command(connection, sql);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static string Parameters(int count) => string.Join(", ", Enumerable.Range(0, count).Select(i => $"$p{i}"));

    // Parameters are $p0, $p1, ... in order; a prepared command keeps its parameters and gets new values.
    private static void Bind(SqliteCommand command, params object[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var name = $"$p{i}";
            if (command.Parameters.Contains(name))
            {
                command.Parameters[name].Value = values[i];
            }
            else
            {
                command.Parameters.AddWithValue(name, values[i]);
            }
        }
    }
}
