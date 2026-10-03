using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Connection;
using ContainerForest.Agent.Docker;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Agent.Tests.Connection;

public sealed class ContainerEventForwarderTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeDockerGateway docker = new();
    private readonly RecordingStreamWriter stream = new();
    private readonly AgentStreamWriter writer;
    private readonly CancellationTokenSource session = new();

    public ContainerEventForwarderTests() => writer = new AgentStreamWriter(stream);

    [Fact]
    public async Task Relevant_event_goes_out_with_the_container_state_after_it()
    {
        docker.Containers.Add(new ContainerInfo { Id = "c1", Name = "web-app-1", State = ContainerState.Exited, StackName = "web" });
        var run = Start();

        await docker.Events.Writer.WriteAsync(new DockerContainerEvent("c1", "exec_start: sh -c 'echo $TOKEN'", At), Ct);
        await docker.Events.Writer.WriteAsync(new DockerContainerEvent("c1", "die", At), Ct);

        var message = (await stream.NextAsync()).ContainerEvent;
        Assert.Equal(("c1", "die", At.ToUnixTimeMilliseconds()), (message.ContainerId, message.Action, message.TimestampUnixMs));
        Assert.Equal((ContainerState.Exited, "web"), (message.Container.State, message.Container.StackName));
        Assert.DoesNotContain(docker.Calls, c => c.Contains("exec", StringComparison.Ordinal));
        await StopAsync(run);
    }

    [Fact]
    public async Task Removed_container_is_not_looked_up()
    {
        var run = Start();

        await docker.Events.Writer.WriteAsync(new DockerContainerEvent("c1", "destroy", At), Ct);

        var message = (await stream.NextAsync()).ContainerEvent;
        Assert.Equal("destroy", message.Action);
        Assert.Null(message.Container);
        Assert.Empty(docker.Calls);
        await StopAsync(run);
    }

    [Fact]
    public async Task Event_goes_out_even_when_the_lookup_fails()
    {
        var run = Start();
        docker.Failure = new DockerUnavailableException(new IOException("socket closed"));

        await docker.Events.Writer.WriteAsync(new DockerContainerEvent("c1", "start", At), Ct);

        var message = (await stream.NextAsync()).ContainerEvent;
        Assert.Equal(("c1", "start"), (message.ContainerId, message.Action));
        Assert.Null(message.Container);
        await StopAsync(run);
    }

    [Fact]
    public async Task Following_resumes_after_docker_went_away_or_ended_the_stream()
    {
        docker.WatchFailure = new DockerUnavailableException(new IOException("connection refused"));
        var run = Start();

        // First watch fails, the second follows events until Docker ends the stream, the third waits for more.
        await docker.Events.Writer.WriteAsync(new DockerContainerEvent("c1", "start", At), Ct);
        Assert.Equal("start", (await stream.NextAsync()).ContainerEvent.Action);
        var ended = docker.Events;
        docker.Events = System.Threading.Channels.Channel.CreateUnbounded<DockerContainerEvent>();
        ended.Writer.Complete();
        await docker.Events.Writer.WriteAsync(new DockerContainerEvent("c2", "create", At), Ct);

        Assert.Equal("c2", (await stream.NextAsync()).ContainerEvent.ContainerId);
        Assert.Equal(3, docker.Watches);
        await StopAsync(run);
    }

    [Theory]
    [InlineData("start", true)]
    [InlineData("die", true)]
    [InlineData("health_status: unhealthy", true)]
    [InlineData("destroy", true)]
    [InlineData("exec_start: sh -c 'cat /run/secrets/db'", false)]
    [InlineData("exec_create: env", false)]
    [InlineData("attach", false)]
    [InlineData("resize", false)]
    [InlineData("kill", false)]
    public void Filter_keeps_state_changes_only(string action, bool relevant) =>
        Assert.Equal(relevant, ContainerEventFilter.IsRelevant(action));

    public void Dispose()
    {
        session.Dispose();
        writer.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task Start() =>
        new ContainerEventForwarder(docker, TimeProvider.System, NullLogger<ContainerEventForwarder>.Instance)
        {
            RetryDelay = TimeSpan.FromMilliseconds(10),
        }.RunAsync(writer, session.Token);

    private async Task StopAsync(Task run)
    {
        await session.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }
}
