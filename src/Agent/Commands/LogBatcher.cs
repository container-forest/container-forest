using ContainerForest.Contracts.Agent;
using Google.Protobuf;

namespace ContainerForest.Agent.Commands;

/// <summary>
/// Collects log lines into LogBatch messages: one message per line would cost a stream write each.
/// A batch is sent when it has <see cref="MaxLines"/> lines or <see cref="MaxBytes"/>, or <see cref="MaxDelay"/>
/// after its first line, so that slow output is not held back.
/// </summary>
/// <remarks>Not thread-safe: lines come from one read loop.</remarks>
public sealed class LogBatcher(ILogSink sink, TimeProvider time)
{
    public const int MaxLines = 200;

    /// <summary>Well under gRPC's 4 MB message limit, whatever the lines hold.</summary>
    public const int MaxBytes = 1024 * 1024;

    public static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(100);

    private List<LogLine> lines = [];
    private int bytes;
    private long firstLineAt;

    /// <summary>Lines sent so far; for the agent log, which never gets the text itself.</summary>
    public int SentLines { get; private set; }

    public async Task AddAsync(LogLine line, CancellationToken cancellationToken)
    {
        var size = CodedOutputStream.ComputeMessageSize(line);
        if (lines.Count > 0 && bytes + size > MaxBytes)
        {
            await FlushAsync(cancellationToken);
        }

        if (lines.Count == 0)
        {
            firstLineAt = time.GetTimestamp();
        }

        lines.Add(line);
        bytes += size;
        if (lines.Count >= MaxLines || time.GetElapsedTime(firstLineAt) >= MaxDelay)
        {
            await FlushAsync(cancellationToken);
        }
    }

    /// <summary>Sends the collected lines, if any.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return;
        }

        // The sink may keep the list (a message holds its lines), so the next batch starts a new one.
        var batch = lines;
        lines = [];
        bytes = 0;
        await sink.SendAsync(batch, cancellationToken);
        SentLines += batch.Count;
    }
}
