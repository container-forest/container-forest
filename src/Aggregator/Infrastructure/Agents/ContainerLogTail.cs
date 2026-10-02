using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Contracts.Agent;
using LogStream = ContainerForest.Aggregator.Application.Agents.LogStream;
using ProtoLogStream = ContainerForest.Contracts.Agent.LogStream;

namespace ContainerForest.Aggregator.Infrastructure.Agents;

/// <summary>
/// Collects the log batches of one StreamContainerLogs request, keeping only the last lines: an agent that sends
/// more than it was asked for cannot make the aggregator hold a container's whole log in memory.
/// </summary>
/// <remarks>Batches arrive on the connection's receive loop while the caller waits, so access is locked.</remarks>
internal sealed class ContainerLogTail(int capacity)
{
    private readonly int capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly Queue<ContainerLogLine> lines = new();
    private readonly Lock gate = new();

    public void Add(LogBatch batch)
    {
        lock (gate)
        {
            foreach (var line in batch.Lines)
            {
                if (lines.Count == capacity)
                {
                    lines.Dequeue();
                }

                lines.Enqueue(ToLine(line));
            }
        }
    }

    /// <summary>The kept lines, oldest first.</summary>
    public IReadOnlyList<ContainerLogLine> ToList()
    {
        lock (gate)
        {
            return lines.ToList();
        }
    }

    private static ContainerLogLine ToLine(LogLine line) => new(
        line.TimestampUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(line.TimestampUnixMs) : null,
        line.Stream == ProtoLogStream.Stderr ? LogStream.Stderr : LogStream.Stdout,
        line.Text);
}
