using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Connection;
using ContainerForest.Agent.Enrollment;
using ContainerForest.Agent.Platform;
using ContainerForest.Contracts.Agent;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Tests.Connection;

public class AgentSessionRunnerTests
{
    private static readonly AgentCredentials Credentials = new("0199a8f0-0000-7000-8000-000000000001", "s3cret");

    private readonly FakeDockerGateway docker = new();
    private readonly FakeGatewayClient gateway = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sends_credentials_and_hello_first()
    {
        var session = CreateRunner().RunAsync(gateway, Credentials, Ct);

        var hello = (await gateway.Sent.NextAsync()).Hello;
        Assert.NotNull(hello);
        Assert.Equal("boot-42", hello.BootId);
        Assert.Equal("29.8.0", hello.DockerVersion);
        Assert.Equal(AgentInfo.Version, hello.AgentVersion);
        Assert.Equal(Credentials.AgentId, gateway.ConnectHeaders!.GetValue(AgentSessionRunner.AgentIdHeader));
        Assert.Equal(Credentials.AgentSecret, gateway.ConnectHeaders!.GetValue(AgentSessionRunner.AgentSecretHeader));

        gateway.Received.End();
        await session;
    }

    [Fact]
    public async Task Connects_with_an_empty_docker_version_when_docker_is_down()
    {
        docker.Failure = new DockerUnavailableException(new IOException("no socket"));

        var session = CreateRunner().RunAsync(gateway, Credentials, Ct);

        Assert.Equal("", (await gateway.Sent.NextAsync()).Hello.DockerVersion);
        gateway.Received.End();
        await session;
    }

    [Fact]
    public async Task Answers_commands_with_their_request_id()
    {
        docker.Containers.Add(new ContainerInfo { Id = "c1", Name = "web-app-1" });
        var session = CreateRunner().RunAsync(gateway, Credentials, Ct);

        gateway.Received.Send(new ControllerMessage { RequestId = "r1", ListContainers = new ListContainers() });
        gateway.Received.Send(new ControllerMessage { RequestId = "r2", RestartContainer = new RestartContainer { ContainerId = "c1", TimeoutSeconds = 10 } });

        var results = new[]
        {
            (await gateway.Sent.NextAsync(m => m.Result is not null)).Result,
            (await gateway.Sent.NextAsync(m => m.Result is not null)).Result,
        }.ToDictionary(r => r.RequestId);
        Assert.Equal("web-app-1", Assert.Single(results["r1"].Containers.Items).Name);
        Assert.Null(results["r2"].Error);

        gateway.Received.End();
        await session;
    }

    [Fact]
    public async Task Sends_heartbeats()
    {
        var session = CreateRunner(heartbeat: TimeSpan.FromMilliseconds(20)).RunAsync(gateway, Credentials, Ct);

        await gateway.Sent.NextAsync(m => m.Heartbeat is not null);
        await gateway.Sent.NextAsync(m => m.Heartbeat is not null);

        gateway.Received.End();
        await session;
    }

    [Fact]
    public async Task Rejected_credentials_surface_as_unauthenticated()
    {
        var session = CreateRunner().RunAsync(gateway, Credentials, Ct);

        gateway.Received.End(new RpcException(new Status(StatusCode.Unauthenticated, "Agent credentials are invalid.")));

        var error = await Assert.ThrowsAsync<RpcException>(() => session);
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
    }

    private AgentSessionRunner CreateRunner(TimeSpan? heartbeat = null)
    {
        var options = Options.Create(new AgentOptions
        {
            ControllerUrl = new Uri("http://localhost:5081"),
            HeartbeatInterval = heartbeat ?? TimeSpan.FromMinutes(1),
        });
        var dispatcher = new CommandDispatcher(
            [
                new ListContainersHandler(docker, NullLogger<ListContainersHandler>.Instance),
                new RestartContainerHandler(docker, NullLogger<RestartContainerHandler>.Instance),
            ],
            NullLogger<CommandDispatcher>.Instance);
        return new AgentSessionRunner(
            dispatcher, docker, new StubBootIds(), options, TimeProvider.System, NullLogger<AgentSessionRunner>.Instance);
    }

    private sealed class StubBootIds : IBootIdProvider
    {
        public string GetBootId() => "boot-42";
    }
}
