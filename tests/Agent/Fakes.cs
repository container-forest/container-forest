using System.Threading.Channels;
using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Docker;
using ContainerForest.Contracts.Agent;
using Grpc.Core;

namespace ContainerForest.Agent.Tests;

/// <summary>Records calls; a call fails with <see cref="Failure"/> when it is set.</summary>
internal sealed class FakeDockerGateway : IDockerGateway
{
    public List<ContainerInfo> Containers { get; } = [];

    public List<string> Calls { get; } = [];

    public AgentCommandException? Failure { get; set; }

    public TimeSpan? LastStopTimeout { get; private set; }

    public Task<string> GetVersionAsync(CancellationToken cancellationToken) => Run("version", "29.8.0");

    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken cancellationToken) =>
        Run<IReadOnlyList<ContainerInfo>>("list", Containers);

    public Task StartContainerAsync(string containerId, CancellationToken cancellationToken) =>
        Run($"start {containerId}", true);

    public Task StopContainerAsync(string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        LastStopTimeout = stopTimeout;
        return Run($"stop {containerId}", true);
    }

    public Task RestartContainerAsync(string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        LastStopTimeout = stopTimeout;
        return Run($"restart {containerId}", true);
    }

    private Task<T> Run<T>(string call, T result)
    {
        Calls.Add(call);
        return Failure is null ? Task.FromResult(result) : Task.FromException<T>(Failure);
    }
}

/// <summary>Collects written messages and fails the test if two writes overlap.</summary>
internal sealed class RecordingStreamWriter(TimeSpan? writeDelay = null) : IClientStreamWriter<AgentMessage>
{
    private readonly Channel<AgentMessage> written = Channel.CreateUnbounded<AgentMessage>();
    private int activeWrites;

    public WriteOptions? WriteOptions { get; set; }

    public int MaxConcurrentWrites { get; private set; }

    public Task WriteAsync(AgentMessage message) => WriteAsync(message, CancellationToken.None);

    public async Task WriteAsync(AgentMessage message, CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref activeWrites);
        MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, active);
        try
        {
            if (writeDelay is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            written.Writer.TryWrite(message);
        }
        finally
        {
            Interlocked.Decrement(ref activeWrites);
        }
    }

    public Task CompleteAsync() => Task.CompletedTask;

    public async Task<AgentMessage> NextAsync(Func<AgentMessage, bool>? filter = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var message = await written.Reader.ReadAsync(timeout.Token);
            if (filter is null || filter(message))
            {
                return message;
            }
        }
    }
}

/// <summary>The aggregator's side of a Connect call: the test pushes commands and ends the stream.</summary>
internal sealed class FakeControllerStream : IAsyncStreamReader<ControllerMessage>
{
    private readonly Channel<ControllerMessage> commands = Channel.CreateUnbounded<ControllerMessage>();

    public ControllerMessage Current { get; private set; } = null!;

    public void Send(ControllerMessage message) => commands.Writer.TryWrite(message);

    public void End(RpcException? error = null) => commands.Writer.TryComplete(error);

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (!await commands.Reader.WaitToReadAsync(cancellationToken))
        {
            return false;
        }

        Current = await commands.Reader.ReadAsync(cancellationToken);
        return true;
    }
}

/// <summary>A gateway client whose calls are served by the test instead of a network.</summary>
internal sealed class FakeGatewayClient : AgentGateway.AgentGatewayClient
{
    public RecordingStreamWriter Sent { get; } = new();

    public FakeControllerStream Received { get; } = new();

    public Metadata? ConnectHeaders { get; private set; }

    public List<EnrollRequest> EnrollRequests { get; } = [];

    public Func<EnrollRequest, EnrollResponse> EnrollHandler { get; set; } =
        _ => new EnrollResponse { AgentId = "0199a8f0-0000-7000-8000-000000000001", AgentSecret = "secret" };

    public override AsyncDuplexStreamingCall<AgentMessage, ControllerMessage> Connect(
        Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
    {
        ConnectHeaders = headers;
        return new AsyncDuplexStreamingCall<AgentMessage, ControllerMessage>(
            Sent, Received, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }

    public override AsyncUnaryCall<EnrollResponse> EnrollAsync(
        EnrollRequest request, Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
    {
        EnrollRequests.Add(request);
        return new AsyncUnaryCall<EnrollResponse>(
            Task.Run(() => EnrollHandler(request), cancellationToken), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => [], () => { });
    }
}

/// <summary>Returns the same value from every <see cref="NextDouble"/> call.</summary>
internal sealed class FixedRandom(double value) : Random
{
    public override double NextDouble() => value;
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>A progress sink that remembers the reported lines.</summary>
internal sealed class RecordingProgress : ICommandProgress
{
    public List<string> Lines { get; } = [];

    public Task ReportAsync(string line, CancellationToken cancellationToken)
    {
        Lines.Add(line);
        return Task.CompletedTask;
    }
}
