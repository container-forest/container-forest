extern alias bot;

using bot::ContainerForest.DiscordBot.Forest;
using ContainerForest.DiscordBot.Tests.Forest;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Proto = ContainerForest.Contracts.Bot;

namespace ContainerForest.DiscordBot.Tests.Notifications;

public sealed class GrpcNotificationSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Watch_MapsNotificationsUntilTheServerEndsTheStream()
    {
        await using var server = await NotificationGatewayServer.StartAsync(Ct);
        server.Gateway.Outgoing.Writer.TryWrite(new Proto.Notification
        {
            Id = "0199a1b2-0000-7000-8000-00000000000a",
            DiscordChannelId = 1_234_567_890_123_456_789,
            Kind = Proto.NotificationKind.DeploymentFailed,
            Severity = Proto.NotificationSeverity.Error,
            OccurredUnixMs = 1_790_000_000_000,
            EnvironmentId = "0199a1b2-0000-7000-8000-000000000001",
            EnvironmentName = "prod",
            Summary = "Deployment of stack 'web' on 'prod' failed: compose failed",
            Deployment = new Proto.DeploymentDetails
            {
                DeploymentId = "0199a1b2-0000-7000-8000-00000000000d",
                StackId = "0199a1b2-0000-7000-8000-00000000000s",
                StackName = "web",
                Error = "compose failed",
            },
        });
        server.Gateway.Outgoing.Writer.TryWrite(new Proto.Notification
        {
            Id = "0199a1b2-0000-7000-8000-00000000000b",
            DiscordChannelId = 42,
            Kind = Proto.NotificationKind.ContainerDied,
            Severity = Proto.NotificationSeverity.Error,
            Container = new Proto.ContainerDetails { ContainerId = "4f1c2a9e8b7d", ContainerName = "web-app-1", ExitCode = 137 },
        });
        server.Gateway.Outgoing.Writer.Complete();

        var received = new List<ForestNotification>();
        await foreach (var notification in server.CreateSource().WatchAsync(Ct))
        {
            received.Add(notification);
        }

        Assert.Equal(2, received.Count);
        var deployment = received[0];
        Assert.Equal(1_234_567_890_123_456_789ul, deployment.DiscordChannelId);
        Assert.Equal(NotificationKind.DeploymentFailed, deployment.Kind);
        Assert.Equal(NotificationSeverity.Error, deployment.Severity);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000), deployment.OccurredAt);
        Assert.Equal("prod", deployment.EnvironmentName);
        Assert.Equal(
            new DeploymentEvent("0199a1b2-0000-7000-8000-00000000000d", "0199a1b2-0000-7000-8000-00000000000s", "web", null, false, "compose failed"),
            deployment.Deployment);
        Assert.Null(deployment.Container);
        Assert.Equal(new ContainerEvent("4f1c2a9e8b7d", "web-app-1", null, null, 137), received[1].Container);
        Assert.Equal([InMemoryAggregator.ServiceToken], server.Gateway.ReceivedTokens);
    }

    [Fact]
    public async Task Watch_EndsQuietly_WhenAnotherStreamReplacesIt()
    {
        await using var server = await NotificationGatewayServer.StartAsync(Ct);
        server.Gateway.StreamFailure = new RpcException(new Status(StatusCode.Aborted, "A newer WatchNotifications call replaced this one."));

        await foreach (var _ in server.CreateSource().WatchAsync(Ct))
        {
            Assert.Fail("Nothing was sent.");
        }

        Assert.Contains(server.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("another instance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Watch_WithRejectedToken_IsUnavailable()
    {
        await using var server = await NotificationGatewayServer.StartAsync(Ct);
        server.Gateway.StreamFailure = ScriptedBotGateway.Fail(
            StatusCode.Unauthenticated, "Invalid bot token.", RpcErrorMapper.InvalidBotTokenReason);

        await Assert.ThrowsAsync<ForestUnavailableException>(async () =>
        {
            await foreach (var _ in server.CreateSource().WatchAsync(Ct))
            {
            }
        });
    }

    [Fact]
    public async Task Report_SendsTheOutcome()
    {
        await using var server = await NotificationGatewayServer.StartAsync(Ct);
        var source = server.CreateSource();

        await source.ReportAsync("n-1", delivered: true, error: null, Ct);
        await source.ReportAsync("n-2", delivered: false, error: "Missing Permissions", Ct);

        Assert.Equal(
            [
                new Proto.ReportDeliveryRequest { NotificationId = "n-1", Delivered = true },
                new Proto.ReportDeliveryRequest { NotificationId = "n-2", Delivered = false, Error = "Missing Permissions" },
            ],
            server.Gateway.Reports);
        Assert.All(server.Gateway.ReceivedTokens, token => Assert.Equal(InMemoryAggregator.ServiceToken, token));
    }
}
