using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Tests.Commands;

public class CancellationTests
{
    private readonly BlockingHandler blocking = new();
    private readonly CommandDispatcher dispatcher;

    public CancellationTests() =>
        dispatcher = new CommandDispatcher([blocking], NullLogger<CommandDispatcher>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Registration_is_removed_when_the_command_finishes()
    {
        var running = new RunningCommands();

        using (var command = running.Begin("r1", Ct))
        {
            Assert.NotNull(command);
            Assert.Equal(1, running.Count);
        }

        Assert.Equal(0, running.Count);
        Assert.False(running.Cancel("r1"));
    }

    [Fact]
    public void Cancel_cancels_only_the_named_command()
    {
        var running = new RunningCommands();
        using var first = running.Begin("r1", Ct)!;
        using var second = running.Begin("r2", Ct)!;

        Assert.True(running.Cancel("r1"));

        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(second.Token.IsCancellationRequested);
        Assert.False(running.Cancel("unknown"));
    }

    [Fact]
    public void Duplicate_request_id_is_refused_and_keeps_the_first_registration()
    {
        var running = new RunningCommands();
        using var first = running.Begin("r1", Ct)!;

        Assert.Null(running.Begin("r1", Ct));
        Assert.True(running.Cancel("r1"));
        Assert.True(first.Token.IsCancellationRequested);
    }

    [Fact]
    public void Session_end_cancels_the_command()
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = new RunningCommands();
        using var command = running.Begin("r1", session.Token)!;

        session.Cancel();

        Assert.True(command.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Cancel_request_ends_the_running_command_with_cancelled()
    {
        var command = dispatcher.DispatchAsync(Message("r1"), new RecordingProgress(), Ct);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var cancelResult = await dispatcher.DispatchAsync(Cancel("r1"), new RecordingProgress(), Ct);
        var result = await command.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Null(cancelResult);
        Assert.Equal("r1", result!.RequestId);
        Assert.Equal(ErrorCode.Cancelled, result.Error.Code);
        Assert.Equal(0, dispatcher.RunningCount);
    }

    [Fact]
    public async Task Cancel_request_for_an_unknown_or_finished_command_is_ignored()
    {
        Assert.Null(await dispatcher.DispatchAsync(Cancel("never-sent"), new RecordingProgress(), Ct));
    }

    [Fact]
    public async Task Duplicate_request_id_is_an_invalid_argument()
    {
        var first = dispatcher.DispatchAsync(Message("r1"), new RecordingProgress(), Ct);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var duplicate = await dispatcher.DispatchAsync(Message("r1"), new RecordingProgress(), Ct);

        Assert.Equal(ErrorCode.InvalidArgument, duplicate!.Error.Code);
        await dispatcher.DispatchAsync(Cancel("r1"), new RecordingProgress(), Ct);
        Assert.Equal(ErrorCode.Cancelled, (await first)!.Error.Code);
    }

    [Fact]
    public async Task Cancelled_deploy_keeps_the_previous_secrets_and_releases_the_stack()
    {
        var store = new FakeStackFileStore();
        var compose = new FakeComposeRunner { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var locks = new StackLocks();
        var options = Options.Create(new AgentOptions { SecretsDirectory = Path.Combine(Path.GetTempPath(), "cf-cancel-test") });
        var deploy = new DeployStackHandler(
            store, compose, new RegistryConfigWriter(options, NullLogger<RegistryConfigWriter>.Instance), locks, options, NullLogger<DeployStackHandler>.Instance);
        var deployDispatcher = new CommandDispatcher([deploy], NullLogger<CommandDispatcher>.Instance);

        var command = deployDispatcher.DispatchAsync(DeployMessage("r1"), new RecordingProgress(), Ct);
        while (compose.Runs.Count == 0)
        {
            await Task.Delay(10, Ct);
        }

        await deployDispatcher.DispatchAsync(Cancel("r1"), new RecordingProgress(), Ct);
        var result = await command.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(ErrorCode.Cancelled, result!.Error.Code);
        Assert.Equal(["write web"], store.Calls);
        Assert.False(locks.IsHeld("web"));
    }

    private static ControllerMessage Message(string requestId) => new() { RequestId = requestId, ListContainers = new ListContainers() };

    private static ControllerMessage Cancel(string target) =>
        new() { RequestId = "c-" + target, CancelRequest = new CancelRequest { TargetRequestId = target } };

    private static ControllerMessage DeployMessage(string requestId) => new()
    {
        RequestId = requestId,
        DeployStack = new DeployStack
        {
            StackName = "web",
            Pull = PullPolicy.Missing,
            Files = { new StackFile { Path = "compose.yaml", Content = ByteString.CopyFromUtf8("services: {}"), Mode = 0b110_100_100 } },
        },
    };

    /// <summary>Runs until it is cancelled.</summary>
    private sealed class BlockingHandler() : AgentCommandHandler<ListContainers>(NullLogger.Instance)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<CommandResult> ExecuteAsync(
            ListContainers command, ICommandProgress progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new CommandResult();
        }
    }
}
