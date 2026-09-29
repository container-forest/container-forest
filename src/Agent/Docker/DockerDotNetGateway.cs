using System.Net.Sockets;
using System.Text.Json;
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

    public void Dispose() => client.Dispose();

    private static uint Seconds(TimeSpan timeout) => (uint)Math.Ceiling(timeout.TotalSeconds);

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
