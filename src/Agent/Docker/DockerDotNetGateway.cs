using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using ContainerForest.Agent.Commands;
using ContainerForest.Contracts.Agent;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace ContainerForest.Agent.Docker;

/// <summary>Adapter from <see cref="IDockerGateway"/> to Docker.DotNet.</summary>
public sealed class DockerDotNetGateway : IDockerGateway, IDisposable
{
    /// <summary>Budget for calls that return at once: version, list, start.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly DockerClient client;

    public DockerDotNetGateway()
        : this(new DockerClientBuilder()
            // Endpoint as the docker CLI resolves it: DOCKER_HOST, then the current context, then the platform default
            // (unix:///var/run/docker.sock or npipe://./pipe/docker_engine).
            // The client's own 100 s timeout would cut off a stop with a long grace period, so each call sets its own.
            .WithTimeout(Timeout.InfiniteTimeSpan)
            .Build())
    {
    }

    public DockerDotNetGateway(DockerClient client)
    {
        this.client = client;
    }

    public Task<string> GetVersionAsync(CancellationToken cancellationToken) =>
        CallAsync(
            null,
            RequestTimeout,
            async ct => (await client.System.GetVersionAsync(ct)).Version ?? "",
            cancellationToken);

    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken cancellationToken) =>
        CallAsync<IReadOnlyList<ContainerInfo>>(
            null,
            RequestTimeout,
            async ct =>
            {
                var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
                return containers.Select(ContainerInfoMapper.ToContainerInfo).ToList();
            },
            cancellationToken);

    public Task<ContainerInfo?> FindContainerAsync(string containerId, CancellationToken cancellationToken) =>
        CallAsync(
            null,
            RequestTimeout,
            async ct =>
            {
                // The id filter matches prefixes too, so the exact id is checked again.
                var containers = await client.Containers.ListContainersAsync(
                    new ContainersListParameters
                    {
                        All = true,
                        Filters = new Dictionary<string, IDictionary<string, bool>> { ["id"] = new Dictionary<string, bool> { [containerId] = true } },
                    },
                    ct);
                return containers.Where(c => c.ID == containerId).Select(ContainerInfoMapper.ToContainerInfo).FirstOrDefault();
            },
            cancellationToken);

    public async Task WatchContainerEventsAsync(
        Func<DockerContainerEvent, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
    {
        // Docker.DotNet reports events through IProgress on its reading thread; a channel hands them to an async
        // consumer in order, so a slow consumer never blocks the reading.
        var events = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var parameters = new ContainerEventsParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>> { ["type"] = new Dictionary<string, bool> { ["container"] = true } },
        };

        var monitor = Task.Run(
            async () =>
            {
                try
                {
                    await CallAsync(
                        null,
                        Timeout.InfiniteTimeSpan,
                        async ct =>
                        {
                            await client.System.MonitorEventsAsync(parameters, new ChannelProgress(events.Writer), ct);
                            return true;
                        },
                        cancellationToken);
                    events.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    events.Writer.TryComplete(ex);
                }
            },
            CancellationToken.None);

        try
        {
            await foreach (var message in events.Reader.ReadAllAsync(cancellationToken))
            {
                if (message.Actor?.ID is { Length: > 0 } id && message.Action is { Length: > 0 } action)
                {
                    await onEvent(
                        new DockerContainerEvent(id, action, TimeOf(message), ExitCodeOf(action, message.Actor.Attributes)),
                        cancellationToken);
                }
            }
        }
        finally
        {
            await monitor;
        }
    }

    public Task StartContainerAsync(string containerId, CancellationToken cancellationToken) =>
        CallAsync(
            containerId,
            RequestTimeout,
            // false means "already running", which is the requested state.
            ct => client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct),
            cancellationToken);

    public Task StopContainerAsync(string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken) =>
        CallAsync(
            containerId,
            RequestTimeout + stopTimeout,
            // false means "already stopped", which is the requested state.
            ct => client.Containers.StopContainerAsync(
                containerId, new ContainerStopParameters { WaitBeforeKillSeconds = Seconds(stopTimeout) }, ct),
            cancellationToken);

    public Task RestartContainerAsync(string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken) =>
        CallAsync(
            containerId,
            RequestTimeout + stopTimeout,
            async ct =>
            {
                await client.Containers.RestartContainerAsync(
                    containerId, new ContainerRestartParameters { WaitBeforeKillSeconds = Seconds(stopTimeout) }, ct);
                return true;
            },
            cancellationToken);

    public async Task ReadContainerLogsAsync(
        string containerId,
        int tailLines,
        DateTimeOffset? since,
        Func<LogLine, CancellationToken, Task> onLine,
        CancellationToken cancellationToken)
    {
        var parameters = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Timestamps = true,
            Follow = false,
            Tail = tailLines > 0 ? tailLines.ToString(CultureInfo.InvariantCulture) : "all",
            Since = since is { } from ? FormatSince(from) : null,
        };

        // Docker.DotNet inspects the container first: with a TTY the stream is not multiplexed and all of it is stdout.
        using var stream = await CallAsync(
            containerId,
            RequestTimeout,
            ct => client.Containers.GetContainerLogsAsync(containerId, parameters, ct),
            cancellationToken);

        // The whole log may take longer than one request budget; the aggregator cancels the read when it stops waiting.
        await CallAsync(
            containerId,
            Timeout.InfiniteTimeSpan,
            async ct =>
            {
                var parser = new ContainerLogParser();
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
                    if (read.EOF)
                    {
                        break;
                    }

                    var target = read.Target == MultiplexedStream.TargetStream.StandardError ? LogStream.Stderr : LogStream.Stdout;
                    foreach (var line in parser.Append(target, buffer.AsSpan(0, read.Count)))
                    {
                        await onLine(line, ct);
                    }
                }

                foreach (var line in parser.Flush())
                {
                    await onLine(line, ct);
                }

                return true;
            },
            cancellationToken);
    }

    public void Dispose() => client.Dispose();

    private static DateTimeOffset TimeOf(Message message) =>
        message.TimeNano is { } nanoseconds and > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(nanoseconds / 1_000_000)
            : DateTimeOffset.UtcNow;

    /// <summary>The "exitCode" attribute of a "die" event: the code the container's main process exited with.</summary>
    public static int? ExitCodeOf(string action, IDictionary<string, string>? attributes) =>
        action == "die" && attributes is not null && attributes.TryGetValue("exitCode", out var text)
        && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)
            ? code
            : null;

    /// <summary>Passes each event on synchronously, in the order Docker reports them (Progress&lt;T&gt; would not keep it).</summary>
    private sealed class ChannelProgress(ChannelWriter<Message> writer) : IProgress<Message>
    {
        public void Report(Message value) => writer.TryWrite(value);
    }

    private static uint Seconds(TimeSpan timeout) => (uint)Math.Ceiling(timeout.TotalSeconds);

    // Docker takes "seconds.nanoseconds" since the epoch, so the bound keeps its milliseconds.
    public static string FormatSince(DateTimeOffset since)
    {
        var ms = Math.Max(0, since.ToUnixTimeMilliseconds());
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 1000}.{ms % 1000:D3}000000");
    }

    /// <summary>Runs a Docker call with its own time budget and translates Docker.DotNet failures.</summary>
    private static async Task<T> CallAsync<T>(
        string? containerId, TimeSpan timeout, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);
        try
        {
            return await call(budget.Token);
        }
        catch (DockerContainerNotFoundException ex) when (containerId is not null)
        {
            throw new ContainerNotFoundException(containerId, ex);
        }
        catch (DockerApiException ex)
        {
            throw new DockerCommandFailedException(DescribeApiError(ex), ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DockerTimeoutException(timeout);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or TimeoutException)
        {
            // Socket or named pipe missing, daemon stopped, connection refused.
            throw new DockerUnavailableException(ex);
        }
    }

    // Docker answers errors with {"message": "..."}; that text is meant for people, e.g. "cannot remove a running container".
    private static string DescribeApiError(DockerApiException ex)
    {
        try
        {
            using var body = JsonDocument.Parse(ex.ResponseBody ?? "");
            if (body.RootElement.TryGetProperty("message", out var message) && message.GetString() is { Length: > 0 } text)
            {
                return $"Docker refused the command: {text}";
            }
        }
        catch (JsonException)
        {
        }

        return $"Docker refused the command with status {(int)ex.StatusCode}.";
    }
}
