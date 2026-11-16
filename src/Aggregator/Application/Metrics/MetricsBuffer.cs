using System.Collections.Concurrent;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Metrics;

/// <summary>
/// What the aggregator knows about resource usage without the database: each environment's samples of the last hour
/// (the finest view, lost on restart), the minute being collected, its latest docker system df and how its agent
/// reads usage. Finished minutes wait in <see cref="TakeFinishedMinutes"/> for the writer. Singleton; agents call
/// <see cref="Add"/> from their gRPC calls while queries read.
/// </summary>
public sealed class MetricsBuffer(TimeProvider time)
{
    /// <summary>How far back raw samples are kept.</summary>
    public static readonly TimeSpan RawWindow = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<EnvironmentId, EnvironmentBuffer> environments = new();
    private readonly ConcurrentQueue<MinuteBatch> finished = new();

    public void SetSource(EnvironmentId environmentId, MetricsSourceKind source, int intervalSeconds)
    {
        var buffer = Get(environmentId);
        lock (buffer)
        {
            buffer.Source = source;
            buffer.IntervalSeconds = intervalSeconds;
        }
    }

    public void Add(MetricsSnapshot snapshot)
    {
        var buffer = Get(snapshot.EnvironmentId);
        lock (buffer)
        {
            var minute = MinuteOf(snapshot.At);
            if (buffer.Minute is { } current && current.Start != minute)
            {
                // A sample older than the minute being collected (a clock step back) starts that minute over.
                finished.Enqueue(current.Finish());
                buffer.Minute = null;
            }

            buffer.Minute ??= new MinuteAccumulator(snapshot.EnvironmentId, minute);
            buffer.Minute.Add(snapshot);

            buffer.Samples.Enqueue(snapshot with { DiskUsage = null });
            var horizon = snapshot.At - RawWindow;
            while (buffer.Samples.TryPeek(out var oldest) && oldest.At < horizon)
            {
                buffer.Samples.Dequeue();
            }

            if (snapshot.DiskUsage is { } usage)
            {
                buffer.DiskUsage = usage;
            }
        }
    }

    /// <summary>
    /// The minutes that ended, including the current minute of an environment whose agent has been silent past its end
    /// (disconnected): otherwise its last minute would wait for the agent to come back.
    /// </summary>
    public IReadOnlyList<MinuteBatch> TakeFinishedMinutes()
    {
        var thisMinute = MinuteOf(time.GetUtcNow());
        foreach (var buffer in environments.Values)
        {
            lock (buffer)
            {
                if (buffer.Minute is { } current && current.Start < thisMinute - 60)
                {
                    finished.Enqueue(current.Finish());
                    buffer.Minute = null;
                }
            }
        }

        var batches = new List<MinuteBatch>();
        while (finished.TryDequeue(out var batch))
        {
            batches.Add(batch);
        }

        return batches;
    }

    /// <summary>Puts batches back after a failed write, so the next run tries them again.</summary>
    public void Return(IEnumerable<MinuteBatch> batches)
    {
        foreach (var batch in batches)
        {
            finished.Enqueue(batch);
        }
    }

    public EnvironmentMetricsState GetState(EnvironmentId environmentId)
    {
        if (!environments.TryGetValue(environmentId, out var buffer))
        {
            return new EnvironmentMetricsState(MetricsSourceKind.Unsupported, 0, null, null);
        }

        lock (buffer)
        {
            return new EnvironmentMetricsState(
                buffer.Source, buffer.IntervalSeconds, buffer.Samples.Count > 0 ? buffer.Samples.Last() : null, buffer.DiskUsage);
        }
    }

    /// <summary>The raw samples at or after <paramref name="from"/>, oldest first.</summary>
    public IReadOnlyList<MetricsSnapshot> GetSamples(EnvironmentId environmentId, DateTimeOffset from)
    {
        if (!environments.TryGetValue(environmentId, out var buffer))
        {
            return [];
        }

        lock (buffer)
        {
            return buffer.Samples.Where(s => s.At >= from).ToList();
        }
    }

    /// <summary>Forgets a deleted environment.</summary>
    public void Remove(EnvironmentId environmentId) => environments.TryRemove(environmentId, out _);

    public static long MinuteOf(DateTimeOffset at) => at.ToUnixTimeSeconds() / 60 * 60;

    private EnvironmentBuffer Get(EnvironmentId environmentId) => environments.GetOrAdd(environmentId, _ => new EnvironmentBuffer());

    private sealed class EnvironmentBuffer
    {
        public MetricsSourceKind Source { get; set; } = MetricsSourceKind.Unsupported;
        public int IntervalSeconds { get; set; }
        public Queue<MetricsSnapshot> Samples { get; } = new();
        public MinuteAccumulator? Minute { get; set; }
        public DiskUsageSample? DiskUsage { get; set; }
    }
}

/// <param name="Latest">The newest sample of the last hour; null when there is none.</param>
public sealed record EnvironmentMetricsState(
    MetricsSourceKind Source, int IntervalSeconds, MetricsSnapshot? Latest, DiskUsageSample? DiskUsage);
