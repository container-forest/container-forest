using ContainerForest.Agent.Commands;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Tests.Commands;

public class LogBatcherTests
{
    private readonly RecordingProgress sink = new();
    private readonly ManualTimeProvider time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Full_batch_is_sent_at_once()
    {
        var batcher = new LogBatcher(sink, time);

        for (var i = 0; i < LogBatcher.MaxLines; i++)
        {
            await batcher.AddAsync(Line("x"), Ct);
        }

        Assert.Equal(LogBatcher.MaxLines, Assert.Single(sink.Batches).Count);
        Assert.Equal(LogBatcher.MaxLines, batcher.SentLines);
    }

    [Fact]
    public async Task Slow_lines_are_sent_after_the_delay()
    {
        var batcher = new LogBatcher(sink, time);

        await batcher.AddAsync(Line("first"), Ct);
        Assert.Empty(sink.Batches);

        time.Advance(LogBatcher.MaxDelay);
        await batcher.AddAsync(Line("second"), Ct);

        Assert.Equal(["first", "second"], Assert.Single(sink.Batches).Select(l => l.Text));
    }

    [Fact]
    public async Task Batch_stays_under_the_byte_limit()
    {
        var batcher = new LogBatcher(sink, time);
        var big = new string('x', 16 * 1024);

        for (var i = 0; i < 100; i++)
        {
            await batcher.AddAsync(Line(big), Ct);
        }

        await batcher.FlushAsync(Ct);

        Assert.True(sink.Batches.Count > 1);
        Assert.All(sink.Batches, b => Assert.True(b.Sum(l => l.CalculateSize() + 3) <= LogBatcher.MaxBytes));
        Assert.Equal(100, sink.Batches.Sum(b => b.Count));
    }

    [Fact]
    public async Task Flush_without_lines_sends_nothing()
    {
        await new LogBatcher(sink, time).FlushAsync(Ct);

        Assert.Empty(sink.Batches);
    }

    private static LogLine Line(string text) => new() { Stream = LogStream.Stdout, Text = text };
}
