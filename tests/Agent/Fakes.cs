using System.Threading.Channels;
using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Docker;
using ContainerForest.Agent.Stacks;
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

    /// <summary>When set, a restart waits for it, as Docker waits for a slow process to stop.</summary>
    public TaskCompletionSource? StopGate { get; set; }

    public Task<string> GetVersionAsync(CancellationToken cancellationToken) => Run("version", "29.8.0");

    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken cancellationToken) =>
        Run<IReadOnlyList<ContainerInfo>>("list", Containers);

    public List<ComposeContainer> ComposeContainers { get; } = [];

    public Task<IReadOnlyList<ComposeContainer>> ListComposeContainersAsync(CancellationToken cancellationToken) =>
        Run<IReadOnlyList<ComposeContainer>>("list compose", ComposeContainers);

    public Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken) =>
        Run($"remove {containerId}", true);

    public Task<ContainerInfo?> FindContainerAsync(string containerId, CancellationToken cancellationToken) =>
        Run($"find {containerId}", Containers.FirstOrDefault(c => c.Id == containerId));

    /// <summary>Events a watch passes on; completing the writer ends the watch as Docker ending the stream would.</summary>
    public Channel<DockerContainerEvent> Events { get; set; } = Channel.CreateUnbounded<DockerContainerEvent>();

    /// <summary>How many times events were followed; a new watch starts after a failure or the end of the stream.</summary>
    public int Watches { get; private set; }

    /// <summary>Thrown by the next watch instead of following events, as when Docker is down.</summary>
    public AgentCommandException? WatchFailure { get; set; }

    public async Task WatchContainerEventsAsync(
        Func<DockerContainerEvent, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
    {
        Watches++;
        if (WatchFailure is { } failure)
        {
            WatchFailure = null;
            throw failure;
        }

        await foreach (var dockerEvent in Events.Reader.ReadAllAsync(cancellationToken))
        {
            await onEvent(dockerEvent, cancellationToken);
        }
    }

    public Task StartContainerAsync(string containerId, CancellationToken cancellationToken) =>
        Run($"start {containerId}", true);

    public Task StopContainerAsync(string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        LastStopTimeout = stopTimeout;
        return Run($"stop {containerId}", true);
    }

    public async Task RestartContainerAsync(string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken)
    {
        LastStopTimeout = stopTimeout;
        if (StopGate is { } gate)
        {
            // A long grace period: the restart waits until the gate opens or the command is cancelled.
            await gate.Task.WaitAsync(cancellationToken);
        }

        await Run($"restart {containerId}", true);
    }

    /// <summary>The lines a log read passes on, in order.</summary>
    public List<LogLine> LogLines { get; } = [];

    /// <summary>When set, a log read waits for it after its lines, as a long log keeps the read open.</summary>
    public TaskCompletionSource? LogGate { get; set; }

    public async Task ReadContainerLogsAsync(
        string containerId,
        int tailLines,
        DateTimeOffset? since,
        Func<LogLine, CancellationToken, Task> onLine,
        CancellationToken cancellationToken)
    {
        await Run($"logs {containerId} tail={tailLines} since={since?.ToUnixTimeMilliseconds()}", true);
        foreach (var line in LogLines)
        {
            await onLine(line, cancellationToken);
        }

        if (LogGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }
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

/// <summary>A clock that moves only when the test calls <see cref="Advance"/>.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => ticks;

    public void Advance(TimeSpan by) => ticks += by.Ticks;
}

/// <summary>A progress sink that remembers the reported lines and the log batches.</summary>
/// <remarks>Thread-safe: compose output is pumped from stdout and stderr at the same time.</remarks>
internal sealed class RecordingProgress : ICommandProgress, ILogSink
{
    private readonly List<string> lines = [];
    private readonly List<IReadOnlyList<LogLine>> batches = [];

    public IReadOnlyList<IReadOnlyList<LogLine>> Batches
    {
        get
        {
            lock (batches)
            {
                return [.. batches];
            }
        }
    }

    public Task SendAsync(IReadOnlyCollection<LogLine> lines, CancellationToken cancellationToken)
    {
        lock (batches)
        {
            batches.Add([.. lines]);
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (lines)
            {
                return [.. lines];
            }
        }
    }

    public Task ReportAsync(string line, CancellationToken cancellationToken)
    {
        lock (lines)
        {
            lines.Add(line);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Records compose runs; a run reports <see cref="Output"/>, then fails with <see cref="Failure"/> when it is set.</summary>
internal sealed class FakeComposeRunner : IComposeRunner
{
    private readonly List<ComposeInvocation> runs = [];

    public IReadOnlyList<ComposeInvocation> Runs
    {
        get
        {
            lock (runs)
            {
                return [.. runs];
            }
        }
    }

    public List<string> Output { get; } = [];

    public AgentCommandException? Failure { get; set; }

    /// <summary>When set, a run waits for it, so a test can start a second operation meanwhile.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Whether DOCKER_CONFIG pointed at an existing directory while compose ran.</summary>
    public bool? DockerConfigExistedDuringRun { get; private set; }

    public async Task RunAsync(ComposeInvocation invocation, ICommandProgress progress, CancellationToken cancellationToken)
    {
        lock (runs)
        {
            runs.Add(invocation);
        }

        if (invocation.Environment.TryGetValue(RegistryConfigWriter.DockerConfigVariable, out var dockerConfig))
        {
            DockerConfigExistedDuringRun = File.Exists(Path.Combine(dockerConfig, "config.json"));
        }

        foreach (var line in Output)
        {
            await progress.ReportAsync(line, cancellationToken);
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>What <see cref="CaptureAsync"/> returns as compose's standard output.</summary>
    public string Captured { get; set; } = "{}";

    /// <summary>The files of the run's directory while compose ran: relative path → content.</summary>
    public Dictionary<string, string> FilesDuringCapture { get; } = [];

    public async Task<string> CaptureAsync(
        ComposeInvocation invocation, ICommandProgress progress, int maxOutputBytes, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(invocation.WorkingDirectory, "*", SearchOption.AllDirectories))
        {
            FilesDuringCapture[Path.GetRelativePath(invocation.WorkingDirectory, file).Replace('\\', '/')] = await File.ReadAllTextAsync(file, cancellationToken);
        }

        await RunAsync(invocation, progress, cancellationToken);
        return Captured.Replace("$DIR", invocation.WorkingDirectory.Replace('\\', '/'), StringComparison.Ordinal);
    }
}

/// <summary>Keeps written layouts in memory and records the secret clean-ups.</summary>
internal sealed class FakeStackFileStore : IStackFileStore
{
    public HashSet<string> Deployed { get; } = [];

    public List<StackLayout> Written { get; } = [];

    public List<string> Calls { get; } = [];

    public bool Exists(string stackName) => Deployed.Contains(stackName);

    /// <summary>What <see cref="WriteAsync"/> reports about files other than the compose file.</summary>
    public bool FilesChanged { get; set; }

    public Task<StackWriteResult> WriteAsync(StackLayout layout, CancellationToken cancellationToken)
    {
        Written.Add(layout);
        Deployed.Add(layout.StackName);
        Calls.Add($"write {layout.StackName}");
        return Task.FromResult(new StackWriteResult(Path.Combine(Path.GetTempPath(), "stacks", layout.StackName), FilesChanged));
    }

    public void DiscardPreviousSecrets(string stackName) => Calls.Add($"discard-previous-secrets {stackName}");

    public void DeleteSecrets(string stackName) => Calls.Add($"delete-secrets {stackName}");
}
