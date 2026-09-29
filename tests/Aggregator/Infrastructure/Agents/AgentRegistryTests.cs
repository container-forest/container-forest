using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Agents;

public class AgentRegistryTests
{
    private readonly AgentRegistry registry = new(NullLogger<AgentRegistry>.Instance);
    private readonly EnvironmentId environmentId = EnvironmentId.New();

    [Fact]
    public void New_connection_replaces_and_closes_the_previous_one()
    {
        var first = NewSession();
        var second = NewSession();

        registry.Open(first);
        registry.Open(second);

        Assert.True(first.IsClosed);
        Assert.True(registry.TryGet(environmentId, out var current));
        Assert.Same(second, current);
    }

    [Fact]
    public void Removing_a_replaced_session_keeps_the_current_one()
    {
        var first = NewSession();
        var second = NewSession();
        registry.Open(first);
        registry.Open(second);

        Assert.False(registry.Remove(first, "test"));
        Assert.True(registry.IsOnline(environmentId));

        Assert.True(registry.Remove(second, "test"));
        Assert.False(registry.IsOnline(environmentId));
    }

    [Fact]
    public void Closed_session_is_not_online()
    {
        var session = NewSession();
        registry.Open(session);

        session.Close("test");

        Assert.False(registry.IsOnline(environmentId));
    }

    private AgentSession NewSession() =>
        new(environmentId, new RecordingWriter(), DateTimeOffset.UnixEpoch, NullLogger.Instance);
}
