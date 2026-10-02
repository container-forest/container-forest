using ContainerForest.DiscordBot.Configuration;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Proto = ContainerForest.Contracts.Bot;

namespace ContainerForest.DiscordBot.Forest;

/// <summary><see cref="IForestClient"/> over the aggregator's BotGateway (src/Contracts/bot/bot.proto).</summary>
public sealed class GrpcForestClient(
    Proto.BotGateway.BotGatewayClient gateway,
    IOptions<BotOptions> options,
    TimeProvider time,
    ILogger<GrpcForestClient> logger) : IForestClient
{
    public const string BotTokenHeader = "x-bot-token";

    public async Task<ForestCaller?> GetCallerAsync(ulong discordUserId, CancellationToken cancellationToken)
    {
        var response = await CallAsync(
            o => gateway.GetCallerAsync(new Proto.GetCallerRequest { DiscordUserId = discordUserId }, o),
            options.Value.QueryTimeout,
            cancellationToken);
        return response.Linked
            ? new ForestCaller(response.UserId, response.UserName, response.IsAdmin, response.IsActive)
            : null;
    }

    public async Task<string> LinkAccountAsync(
        ulong discordUserId, string discordUserName, string code, CancellationToken cancellationToken)
    {
        var request = new Proto.LinkAccountRequest
        {
            DiscordUserId = discordUserId,
            DiscordUserName = discordUserName,
            Code = code.Trim(),
        };
        var response = await CallAsync(o => gateway.LinkAccountAsync(request, o), options.Value.QueryTimeout, cancellationToken);
        return response.UserName;
    }

    public async Task<IReadOnlyList<ForestEnvironment>> ListEnvironmentsAsync(
        ulong discordUserId, CancellationToken cancellationToken)
    {
        var response = await CallAsync(
            o => gateway.ListEnvironmentsAsync(new Proto.ListEnvironmentsRequest { DiscordUserId = discordUserId }, o),
            options.Value.QueryTimeout,
            cancellationToken);
        return [.. response.Environments.Select(e => new ForestEnvironment(e.Id, e.Name, e.Online))];
    }

    public async Task<IReadOnlyList<ForestContainer>> ListContainersAsync(
        ulong discordUserId, string environmentId, CancellationToken cancellationToken)
    {
        var request = new Proto.ListContainersRequest { DiscordUserId = discordUserId, EnvironmentId = environmentId };
        var response = await CallAsync(
            o => gateway.ListContainersAsync(request, o), options.Value.QueryTimeout, cancellationToken);
        return [.. response.Containers.Select(ToContainer)];
    }

    public async Task ControlContainerAsync(
        ulong discordUserId,
        string environmentId,
        string container,
        ContainerAction action,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var request = new Proto.ControlContainerRequest
        {
            DiscordUserId = discordUserId,
            EnvironmentId = environmentId,
            Container = container,
            Action = ToProto(action),
        };
        if (timeoutSeconds is { } seconds)
        {
            request.TimeoutSeconds = seconds;
        }

        await CallAsync(o => gateway.ControlContainerAsync(request, o), options.Value.ActionTimeout, cancellationToken);
    }

    public async Task<IReadOnlyList<ForestStack>> ListStacksAsync(
        ulong discordUserId, string environmentId, CancellationToken cancellationToken)
    {
        var request = new Proto.ListStacksRequest { DiscordUserId = discordUserId, EnvironmentId = environmentId };
        var response = await CallAsync(o => gateway.ListStacksAsync(request, o), options.Value.QueryTimeout, cancellationToken);
        return [.. response.Stacks.Select(ToStack)];
    }

    public async Task<ForestStackResult> ControlStackAsync(
        ulong discordUserId,
        string environmentId,
        string stack,
        StackAction action,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var request = new Proto.ControlStackRequest
        {
            DiscordUserId = discordUserId,
            EnvironmentId = environmentId,
            Stack = stack,
            Action = action switch
            {
                StackAction.Restart => Proto.StackAction.Restart,
                StackAction.Down => Proto.StackAction.Down,
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
            },
        };
        if (timeoutSeconds is { } seconds)
        {
            request.TimeoutSeconds = seconds;
        }

        var response = await CallAsync(o => gateway.ControlStackAsync(request, o), options.Value.StackActionTimeout, cancellationToken);
        return new ForestStackResult(response.Error is "" ? null : response.Error, [.. response.Output]);
    }

    /// <summary>Adds the token and a deadline, and turns gRPC failures into <see cref="ForestException"/>.</summary>
    private async Task<TResponse> CallAsync<TResponse>(
        Func<CallOptions, AsyncUnaryCall<TResponse>> call, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var callOptions = new CallOptions(
            headers: new Metadata { { BotTokenHeader, options.Value.ServiceToken! } },
            deadline: time.GetUtcNow().UtcDateTime + timeout,
            cancellationToken: cancellationToken);
        try
        {
            return await call(callOptions);
        }
        catch (RpcException ex) when (cancellationToken.IsCancellationRequested)
        {
            // gRPC reports a cancelled call as RpcException(Cancelled), not OperationCanceledException; a reset
            // stream racing with the cancellation may even arrive as another code.
            throw new OperationCanceledException("The call to the aggregator was cancelled.", ex, cancellationToken);
        }
        catch (RpcException ex)
        {
            var failure = RpcErrorMapper.ToForestException(ex);
            if (failure is ForestUnavailableException unavailable)
            {
                logger.LogError(
                    "Cannot use the aggregator at {ControllerUrl}: {Diagnostic}", options.Value.ControllerUrl, unavailable.Diagnostic);
            }

            throw failure;
        }
    }

    private static ForestContainer ToContainer(Proto.Container c) => new(
        c.Id,
        c.Name,
        ToState(c.State),
        c.Image,
        c.StackName is "" ? null : c.StackName,
        c.ServiceName is "" ? null : c.ServiceName,
        c.StatusText,
        c.CanControl);

    private static ForestStack ToStack(Proto.Stack s) => new(
        s.Id,
        s.Name,
        s.IsSystem,
        s.DeployedCommit is "" ? null : s.DeployedCommit,
        s.DeployedUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(s.DeployedUnixMs) : null,
        s.CanRestart,
        s.CanTakeDown);

    private static ContainerState ToState(Proto.ContainerState state) => state switch
    {
        Proto.ContainerState.Created => ContainerState.Created,
        Proto.ContainerState.Running => ContainerState.Running,
        Proto.ContainerState.Paused => ContainerState.Paused,
        Proto.ContainerState.Restarting => ContainerState.Restarting,
        Proto.ContainerState.Removing => ContainerState.Removing,
        Proto.ContainerState.Exited => ContainerState.Exited,
        Proto.ContainerState.Dead => ContainerState.Dead,
        _ => ContainerState.Unknown,
    };

    private static Proto.ContainerAction ToProto(ContainerAction action) => action switch
    {
        ContainerAction.Start => Proto.ContainerAction.Start,
        ContainerAction.Stop => Proto.ContainerAction.Stop,
        ContainerAction.Restart => Proto.ContainerAction.Restart,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };
}
