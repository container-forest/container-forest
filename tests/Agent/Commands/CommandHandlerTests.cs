using ContainerForest.Agent.Commands;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Agent.Tests.Commands;

public class CommandHandlerTests
{
    private readonly FakeDockerGateway docker = new();
    private readonly RecordingProgress progress = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Handlers_derive_the_handled_command_from_their_type()
    {
        Assert.Equal(ControllerMessage.BodyOneofCase.ListContainers, new ListContainersHandler(docker, NullLogger<ListContainersHandler>.Instance).Handles);
        Assert.Equal(ControllerMessage.BodyOneofCase.StartContainer, Start().Handles);
        Assert.Equal(ControllerMessage.BodyOneofCase.StopContainer, Stop().Handles);
        Assert.Equal(ControllerMessage.BodyOneofCase.RestartContainer, Restart().Handles);
    }

    [Fact]
    public async Task List_returns_the_containers_with_the_request_id()
    {
        docker.Containers.Add(new ContainerInfo { Id = "c1", Name = "web-app-1", State = ContainerState.Running });
        var handler = new ListContainersHandler(docker, NullLogger<ListContainersHandler>.Instance);

        var result = await handler.HandleAsync(new ControllerMessage { RequestId = "r1", ListContainers = new ListContainers() }, progress, Ct);

        Assert.Equal("r1", result.RequestId);
        Assert.Null(result.Error);
        Assert.Equal("web-app-1", Assert.Single(result.Containers.Items).Name);
    }

    [Fact]
    public async Task Docker_unavailable_is_reported_with_its_code_and_the_request_id()
    {
        docker.Failure = new DockerUnavailableException(new IOException("pipe not found"));
        var handler = new ListContainersHandler(docker, NullLogger<ListContainersHandler>.Instance);

        var result = await handler.HandleAsync(new ControllerMessage { RequestId = "r2", ListContainers = new ListContainers() }, progress, Ct);

        Assert.Equal("r2", result.RequestId);
        Assert.Equal(ErrorCode.DockerUnavailable, result.Error.Code);
        Assert.Equal("Docker is not reachable on this host.", result.Error.Message);
    }

    [Fact]
    public async Task Unexpected_exceptions_become_internal_errors_without_details()
    {
        var handler = new ThrowingHandler(new InvalidOperationException("connection string with password=hunter2"));

        var result = await handler.HandleAsync(new ControllerMessage { RequestId = "r3", ListContainers = new ListContainers() }, progress, Ct);

        Assert.Equal(ErrorCode.Internal, result.Error.Code);
        Assert.DoesNotContain("hunter2", result.Error.Message);
        Assert.Equal("r3", result.RequestId);
    }

    [Fact]
    public async Task Start_passes_the_container_to_docker()
    {
        var result = await Start().HandleAsync(Message(new StartContainer { ContainerId = "c1" }), progress, Ct);

        Assert.Null(result.Error);
        Assert.Equal("start c1", Assert.Single(docker.Calls));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(0)]
    public async Task Stop_and_restart_pass_the_timeout_to_docker(int seconds)
    {
        await Stop().HandleAsync(Message(new StopContainer { ContainerId = "c1", TimeoutSeconds = seconds }), progress, Ct);
        Assert.Equal(TimeSpan.FromSeconds(seconds), docker.LastStopTimeout);

        await Restart().HandleAsync(Message(new RestartContainer { ContainerId = "c1", TimeoutSeconds = seconds }), progress, Ct);
        Assert.Equal(TimeSpan.FromSeconds(seconds), docker.LastStopTimeout);
    }

    [Fact]
    public async Task Negative_timeout_is_an_invalid_argument()
    {
        var result = await Stop().HandleAsync(Message(new StopContainer { ContainerId = "c1", TimeoutSeconds = -1 }), progress, Ct);

        Assert.Equal(ErrorCode.InvalidArgument, result.Error.Code);
        Assert.Empty(docker.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../images/create")]
    [InlineData("c1/stop")]
    [InlineData("c1\n")]
    [InlineData(".hidden")]
    public async Task Invalid_container_ids_never_reach_docker(string containerId)
    {
        var result = await Restart().HandleAsync(Message(new RestartContainer { ContainerId = containerId, TimeoutSeconds = 10 }), progress, Ct);

        Assert.Equal(ErrorCode.InvalidArgument, result.Error.Code);
        Assert.Empty(docker.Calls);
    }

    [Theory]
    [InlineData("3f4a9c1b2d7e")]
    [InlineData("web-app-1")]
    [InlineData("my_stack.worker.1")]
    public async Task Container_ids_and_names_are_accepted(string containerId)
    {
        var result = await Start().HandleAsync(Message(new StartContainer { ContainerId = containerId }), progress, Ct);

        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Missing_container_is_not_found()
    {
        docker.Failure = new ContainerNotFoundException("c9");

        var result = await Stop().HandleAsync(Message(new StopContainer { ContainerId = "c9", TimeoutSeconds = 10 }), progress, Ct);

        Assert.Equal(ErrorCode.NotFound, result.Error.Code);
        Assert.Equal("Container 'c9' was not found.", result.Error.Message);
    }

    [Fact]
    public async Task Handler_rejects_a_message_of_another_type()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Start().HandleAsync(Message(new StopContainer { ContainerId = "c1" }), progress, Ct));
    }

    private StartContainerHandler Start() => new(docker, NullLogger<StartContainerHandler>.Instance);

    private StopContainerHandler Stop() => new(docker, NullLogger<StopContainerHandler>.Instance);

    private RestartContainerHandler Restart() => new(docker, NullLogger<RestartContainerHandler>.Instance);

    private static ControllerMessage Message(StartContainer command) => new() { RequestId = "r", StartContainer = command };

    private static ControllerMessage Message(StopContainer command) => new() { RequestId = "r", StopContainer = command };

    private static ControllerMessage Message(RestartContainer command) => new() { RequestId = "r", RestartContainer = command };

    private sealed class ThrowingHandler(Exception exception) : AgentCommandHandler<ListContainers>(NullLogger.Instance)
    {
        protected override Task<CommandResult> ExecuteAsync(ListContainers command, ICommandProgress progress, CancellationToken cancellationToken) =>
            Task.FromException<CommandResult>(exception);
    }
}
