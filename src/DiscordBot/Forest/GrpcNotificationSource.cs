using System.Runtime.CompilerServices;
using ContainerForest.DiscordBot.Configuration;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Proto = ContainerForest.Contracts.Bot;

namespace ContainerForest.DiscordBot.Forest;

/// <summary><see cref="INotificationSource"/> over WatchNotifications and ReportDelivery of the aggregator's BotGateway.</summary>
public sealed class GrpcNotificationSource(
    Proto.BotGateway.BotGatewayClient gateway,
    IOptions<BotOptions> options,
    TimeProvider time,
    ILogger<GrpcNotificationSource> logger) : INotificationSource
{
    public async IAsyncEnumerable<ForestNotification> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // No deadline: the stream stays open for as long as the bot runs. The channel's keep-alive pings notice a dead connection.
        using var call = gateway.WatchNotifications(
            new Proto.WatchNotificationsRequest(), new CallOptions(Headers(), cancellationToken: cancellationToken));

        while (true)
        {
            bool received;
            try
            {
                received = await call.ResponseStream.MoveNext(cancellationToken);
            }
            catch (RpcException ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The notification stream was cancelled.", ex, cancellationToken);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Aborted)
            {
                logger.LogWarning(
                    "The aggregator closed the notification stream: {Reason}. Is another instance of the bot running with the same token?",
                    ex.Status.Detail);
                yield break;
            }
            catch (RpcException ex)
            {
                throw Translate(ex);
            }

            if (!received)
            {
                yield break;
            }

            yield return ToNotification(call.ResponseStream.Current);
        }
    }

    public async Task ReportAsync(string notificationId, bool delivered, string? error, CancellationToken cancellationToken)
    {
        var request = new Proto.ReportDeliveryRequest { NotificationId = notificationId, Delivered = delivered, Error = error ?? "" };
        var callOptions = new CallOptions(
            Headers(), deadline: time.GetUtcNow().UtcDateTime + options.Value.QueryTimeout, cancellationToken: cancellationToken);
        try
        {
            await gateway.ReportDeliveryAsync(request, callOptions);
        }
        catch (RpcException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The delivery report was cancelled.", ex, cancellationToken);
        }
        catch (RpcException ex)
        {
            throw Translate(ex);
        }
    }

    private Metadata Headers() => new() { { GrpcForestClient.BotTokenHeader, options.Value.ServiceToken! } };

    private ForestException Translate(RpcException ex)
    {
        var failure = RpcErrorMapper.ToForestException(ex);
        if (failure is ForestUnavailableException unavailable)
        {
            logger.LogError("Cannot use the aggregator at {ControllerUrl}: {Diagnostic}", options.Value.ControllerUrl, unavailable.Diagnostic);
        }

        return failure;
    }

    private static ForestNotification ToNotification(Proto.Notification n) => new(
        n.Id,
        n.DiscordChannelId,
        ToKind(n.Kind),
        ToSeverity(n.Severity),
        DateTimeOffset.FromUnixTimeMilliseconds(n.OccurredUnixMs),
        n.EnvironmentId,
        n.EnvironmentName,
        n.Summary,
        n.DetailsCase == Proto.Notification.DetailsOneofCase.Deployment
            ? new DeploymentEvent(
                n.Deployment.DeploymentId,
                n.Deployment.StackId,
                n.Deployment.StackName,
                NullIfEmpty(n.Deployment.CommitSha),
                n.Deployment.PulledImages,
                NullIfEmpty(n.Deployment.Error))
            : null,
        n.DetailsCase == Proto.Notification.DetailsOneofCase.Container
            ? new ContainerEvent(
                n.Container.ContainerId,
                n.Container.ContainerName,
                NullIfEmpty(n.Container.StackName),
                NullIfEmpty(n.Container.ServiceName),
                n.Container.HasExitCode ? n.Container.ExitCode : null,
                n.Container.System)
            : null);

    private static string? NullIfEmpty(string value) => value is "" ? null : value;

    private static NotificationKind ToKind(Proto.NotificationKind kind) => kind switch
    {
        Proto.NotificationKind.DeploymentSucceeded => NotificationKind.DeploymentSucceeded,
        Proto.NotificationKind.DeploymentFailed => NotificationKind.DeploymentFailed,
        Proto.NotificationKind.ContainerDied => NotificationKind.ContainerDied,
        Proto.NotificationKind.ContainerUnhealthy => NotificationKind.ContainerUnhealthy,
        Proto.NotificationKind.AgentOffline => NotificationKind.AgentOffline,
        Proto.NotificationKind.AgentOnline => NotificationKind.AgentOnline,
        _ => NotificationKind.Unknown,
    };

    private static NotificationSeverity ToSeverity(Proto.NotificationSeverity severity) => severity switch
    {
        Proto.NotificationSeverity.Info => NotificationSeverity.Info,
        Proto.NotificationSeverity.Warning => NotificationSeverity.Warning,
        Proto.NotificationSeverity.Error => NotificationSeverity.Error,
        _ => NotificationSeverity.Unknown,
    };
}
