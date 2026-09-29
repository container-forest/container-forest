using ContainerForest.Agent.Connection;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Agent.Tests.Connection;

public class AgentStreamWriterTests
{
    [Fact]
    public async Task Concurrent_writes_reach_the_stream_one_at_a_time()
    {
        var stream = new RecordingStreamWriter(writeDelay: TimeSpan.FromMilliseconds(5));
        using var writer = new AgentStreamWriter(stream);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => writer.WriteAsync(new AgentMessage { Heartbeat = new Heartbeat() }, TestContext.Current.CancellationToken))));

        Assert.Equal(1, stream.MaxConcurrentWrites);
        for (var i = 0; i < 20; i++)
        {
            await stream.NextAsync();
        }
    }
}
