using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Live;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Contracts.Agent;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using ContainerState = ContainerForest.Aggregator.Application.Agents.ContainerState;
using LogStream = ContainerForest.Aggregator.Application.Agents.LogStream;
using ProtoContainerState = ContainerForest.Contracts.Agent.ContainerState;

namespace ContainerForest.Aggregator.Infrastructure.Agents;

/// <summary>
/// Adapter from <see cref="IAgentChannel"/> to the agent protocol. After a command that can change containers, open
/// panels are told to reload them, whether the command succeeded or not: a failed compose may have changed some.
/// </summary>
internal sealed class GrpcAgentChannel(AgentRegistry registry, IOptions<AgentGatewayOptions> options, ILiveUpdates live)
    : IAgentChannel
{
    public bool IsOnline(EnvironmentId environmentId) => registry.IsOnline(environmentId);

    public void Disconnect(EnvironmentId environmentId, string reason) => registry.Close(environmentId, reason);

    public async Task<IReadOnlyList<ContainerSnapshot>> ListContainersAsync(
        EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var result = await SendAsync(
            environmentId, new ControllerMessage { ListContainers = new ListContainers() }, options.Value.CommandTimeout, cancellationToken);
        if (result.PayloadCase != CommandResult.PayloadOneofCase.Containers)
        {
            throw new AgentCommandFailedException(environmentId, AgentErrorKind.Internal, "Agent returned no container list.");
        }

        return result.Containers.Items.Select(ToSnapshot).ToList();
    }

    public Task StartContainerAsync(EnvironmentId environmentId, string containerId, CancellationToken cancellationToken) =>
        ChangeContainersAsync(environmentId, () => SendAsync(
            environmentId,
            new ControllerMessage { StartContainer = new StartContainer { ContainerId = containerId } },
            options.Value.CommandTimeout,
            cancellationToken));

    public Task StopContainerAsync(
        EnvironmentId environmentId, string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken) =>
        ChangeContainersAsync(environmentId, () => SendAsync(
            environmentId,
            new ControllerMessage { StopContainer = new StopContainer { ContainerId = containerId, TimeoutSeconds = Seconds(stopTimeout) } },
            WaitFor(stopTimeout),
            cancellationToken));

    public Task RestartContainerAsync(
        EnvironmentId environmentId, string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken) =>
        ChangeContainersAsync(environmentId, () => SendAsync(
            environmentId,
            new ControllerMessage { RestartContainer = new RestartContainer { ContainerId = containerId, TimeoutSeconds = Seconds(stopTimeout) } },
            WaitFor(stopTimeout),
            cancellationToken));

    public Task DeployStackAsync(
        EnvironmentId environmentId,
        StackBundle bundle,
        Action<string> onProgress,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var command = new DeployStack
        {
            StackName = bundle.StackName.Value,
            CommitSha = bundle.Commit.Value,
            Pull = bundle.Pull == ImagePullPolicy.Always ? PullPolicy.Always : PullPolicy.Missing,
        };
        command.Files.AddRange(bundle.Files.Select(f => new StackFile
        {
            Path = f.Path.Value,
            Content = ByteString.CopyFrom(f.Content),
            Mode = (uint)f.Mode,
            Secret = f.IsSecret,
        }));
        command.RegistryAuths.AddRange(bundle.RegistryLogins.Select(l => new RegistryAuth
        {
            Server = l.Server,
            Username = l.Username,
            Password = l.Password,
        }));

        return ChangeContainersAsync(
            environmentId, () => SendAsync(environmentId, new ControllerMessage { DeployStack = command }, timeout, cancellationToken, onProgress));
    }

    public Task RestartStackAsync(
        EnvironmentId environmentId,
        StackName stackName,
        TimeSpan stopTimeout,
        Action<string> onProgress,
        CancellationToken cancellationToken) =>
        ChangeContainersAsync(environmentId, () => SendAsync(
            environmentId,
            new ControllerMessage { RestartStack = new RestartStack { StackName = stackName.Value, TimeoutSeconds = Seconds(stopTimeout) } },
            options.Value.StackCommandTimeout,
            cancellationToken,
            onProgress));

    public Task DownStackAsync(
        EnvironmentId environmentId,
        StackName stackName,
        TimeSpan stopTimeout,
        Action<string> onProgress,
        CancellationToken cancellationToken) =>
        ChangeContainersAsync(environmentId, () => SendAsync(
            environmentId,
            new ControllerMessage { DownStack = new DownStack { StackName = stackName.Value, TimeoutSeconds = Seconds(stopTimeout) } },
            options.Value.StackCommandTimeout,
            cancellationToken,
            onProgress));

    public async Task<IReadOnlyList<ContainerLogLine>> ReadContainerLogsAsync(
        EnvironmentId environmentId, string containerId, ContainerLogQuery query, CancellationToken cancellationToken)
    {
        var command = new StreamContainerLogs
        {
            ContainerId = containerId,
            TailLines = query.TailLines,
            Follow = false,
            SinceUnixMs = query.Since?.ToUnixTimeMilliseconds() ?? 0,
        };

        var tail = new ContainerLogTail(query.TailLines);
        await SendAsync(
            environmentId,
            new ControllerMessage { StreamContainerLogs = command },
            options.Value.CommandTimeout,
            cancellationToken,
            onLogs: tail.Add);
        return tail.ToList();
    }

    private async Task ChangeContainersAsync(EnvironmentId environmentId, Func<Task<CommandResult>> send)
    {
        try
        {
            await send();
        }
        catch (AgentOfflineException)
        {
            // Nothing reached the host.
            throw;
        }
        catch
        {
            await live.ContainersChangedAsync(environmentId);
            throw;
        }

        await live.ContainersChangedAsync(environmentId);
    }

    // Docker may wait the whole stop timeout before killing the process; the answer comes after that.
    private TimeSpan WaitFor(TimeSpan stopTimeout) => options.Value.CommandTimeout + stopTimeout;

    private static int Seconds(TimeSpan timeout) => (int)Math.Ceiling(timeout.TotalSeconds);

    private async Task<CommandResult> SendAsync(
        EnvironmentId environmentId,
        ControllerMessage command,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? onProgress = null,
        Action<LogBatch>? onLogs = null)
    {
        if (!registry.TryGet(environmentId, out var session))
        {
            throw new AgentOfflineException(environmentId);
        }

        var result = await session.RequestAsync(command, timeout, cancellationToken, onProgress, onLogs);
        if (result.Error is { } error)
        {
            throw new AgentCommandFailedException(environmentId, ToErrorKind(error.Code), error.Message);
        }

        return result;
    }

    private static ContainerSnapshot ToSnapshot(ContainerInfo info) => new(
        info.Id,
        info.Name,
        ToState(info.State),
        info.Image,
        NullIfEmpty(info.StackName),
        NullIfEmpty(info.ServiceName),
        info.StatusText,
        info.CreatedUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(info.CreatedUnixMs) : null,
        info.System);

    private static ContainerState ToState(ProtoContainerState state) => state switch
    {
        ProtoContainerState.Created => ContainerState.Created,
        ProtoContainerState.Running => ContainerState.Running,
        ProtoContainerState.Paused => ContainerState.Paused,
        ProtoContainerState.Restarting => ContainerState.Restarting,
        ProtoContainerState.Removing => ContainerState.Removing,
        ProtoContainerState.Exited => ContainerState.Exited,
        ProtoContainerState.Dead => ContainerState.Dead,
        _ => ContainerState.Unknown,
    };

    private static AgentErrorKind ToErrorKind(ErrorCode code) => code switch
    {
        ErrorCode.InvalidArgument => AgentErrorKind.InvalidArgument,
        ErrorCode.NotFound => AgentErrorKind.NotFound,
        ErrorCode.DockerUnavailable => AgentErrorKind.DockerUnavailable,
        ErrorCode.CommandFailed => AgentErrorKind.CommandFailed,
        ErrorCode.Timeout => AgentErrorKind.Timeout,
        ErrorCode.Cancelled => AgentErrorKind.Cancelled,
        ErrorCode.Busy => AgentErrorKind.Busy,
        ErrorCode.Internal => AgentErrorKind.Internal,
        ErrorCode.Unsupported => AgentErrorKind.Unsupported,
        _ => AgentErrorKind.Unknown,
    };

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
