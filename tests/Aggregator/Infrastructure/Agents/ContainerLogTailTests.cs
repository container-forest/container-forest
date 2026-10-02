using ContainerForest.Aggregator.Infrastructure.Agents;
using ContainerForest.Contracts.Agent;
using LogStream = ContainerForest.Aggregator.Application.Agents.LogStream;
using ProtoLogStream = ContainerForest.Contracts.Agent.LogStream;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Agents;

public class ContainerLogTailTests
{
    [Fact]
    public void Keeps_only_the_last_lines_across_batches()
    {
        var tail = new ContainerLogTail(capacity: 3);

        tail.Add(Batch("1", "2"));
        tail.Add(Batch("3", "4", "5"));

        Assert.Equal(["3", "4", "5"], tail.ToList().Select(l => l.Text));
    }

    [Fact]
    public void Converts_stream_and_timestamp()
    {
        var tail = new ContainerLogTail(capacity: 10);

        tail.Add(new LogBatch
        {
            Lines =
            {
                new LogLine { Stream = ProtoLogStream.Stderr, TimestampUnixMs = 1_000, Text = "boom" },
                new LogLine { Stream = ProtoLogStream.Unspecified, Text = "no time" },
            },
        });

        var lines = tail.ToList();
        Assert.Equal((DateTimeOffset.FromUnixTimeMilliseconds(1_000), LogStream.Stderr), (lines[0].At!.Value, lines[0].Stream));
        Assert.Equal(((DateTimeOffset?)null, LogStream.Stdout), (lines[1].At, lines[1].Stream));
    }

    [Fact]
    public void Capacity_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainerLogTail(0));

    private static LogBatch Batch(params string[] texts)
    {
        var batch = new LogBatch();
        batch.Lines.AddRange(texts.Select(t => new LogLine { Stream = ProtoLogStream.Stdout, Text = t }));
        return batch;
    }
}
