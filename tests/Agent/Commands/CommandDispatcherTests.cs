using ContainerForest.Agent.Commands;
using ContainerForest.Contracts.Agent;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Agent.Tests.Commands;

public class CommandDispatcherTests
{
    private readonly FakeDockerGateway docker = new();
    private readonly CommandDispatcher dispatcher;

    public CommandDispatcherTests() =>
        dispatcher = new CommandDispatcher(
            [new ListContainersHandler(docker, NullLogger<ListContainersHandler>.Instance)],
            NullLogger<CommandDispatcher>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Routes_the_command_to_its_handler()
    {
        var result = await dispatcher.DispatchAsync(
            new ControllerMessage { RequestId = "r1", ListContainers = new ListContainers() }, new RecordingProgress(), Ct);

        Assert.Equal("r1", result!.RequestId);
        Assert.Equal(["list"], docker.Calls);
    }

    [Fact]
    public async Task Command_without_a_handler_is_unsupported()
    {
        var result = await dispatcher.DispatchAsync(
            new ControllerMessage { RequestId = "r2", DownStack = new DownStack { StackName = "web" } }, new RecordingProgress(), Ct);

        Assert.Equal("r2", result!.RequestId);
        Assert.Equal(ErrorCode.Unsupported, result.Error.Code);
    }

    [Fact]
    public async Task Command_unknown_to_this_protocol_version_is_unsupported()
    {
        // A newer aggregator sends a oneof field this agent does not know; it parses as an unknown field.
        var bytes = new ControllerMessage { RequestId = "r3" }.ToByteArray()
            .Concat(new byte[] { 0xFA, 0x06, 0x00 }) // field 111, length-delimited, empty
            .ToArray();
        var message = ControllerMessage.Parser.ParseFrom(bytes);

        var result = await dispatcher.DispatchAsync(message, new RecordingProgress(), Ct);

        Assert.Equal(ControllerMessage.BodyOneofCase.None, message.BodyCase);
        Assert.Equal("r3", result!.RequestId);
        Assert.Equal(ErrorCode.Unsupported, result.Error.Code);
    }

    [Fact]
    public async Task Cancel_request_gets_no_result()
    {
        var result = await dispatcher.DispatchAsync(
            new ControllerMessage { RequestId = "r4", CancelRequest = new CancelRequest { TargetRequestId = "r1" } }, new RecordingProgress(), Ct);

        Assert.Null(result);
    }

    [Fact]
    public void Two_handlers_for_one_command_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new CommandDispatcher(
            [
                new ListContainersHandler(docker, NullLogger<ListContainersHandler>.Instance),
                new ListContainersHandler(docker, NullLogger<ListContainersHandler>.Instance),
            ],
            NullLogger<CommandDispatcher>.Instance));
    }
}
