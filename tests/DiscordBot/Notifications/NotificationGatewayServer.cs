extern alias bot;

using System.Threading.Channels;
using bot::ContainerForest.DiscordBot.Configuration;
using bot::ContainerForest.DiscordBot.Forest;
using ContainerForest.DiscordBot.Tests.Forest;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proto = ContainerForest.Contracts.Bot;

namespace ContainerForest.DiscordBot.Tests.Notifications;

/// <summary>The notification half of BotGateway in memory: the test writes what the stream sends and reads the reports.</summary>
internal sealed class NotificationGatewayServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly GrpcChannel channel;

    private NotificationGatewayServer(WebApplication app, ScriptedNotificationGateway gateway)
    {
        this.app = app;
        Gateway = gateway;
        var server = app.GetTestServer();
        channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
    }

    public ScriptedNotificationGateway Gateway { get; }

    public RecordingLogger<GrpcNotificationSource> Logger { get; } = new();

    public static async Task<NotificationGatewayServer> StartAsync(CancellationToken cancellationToken)
    {
        var gateway = new ScriptedNotificationGateway();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(gateway);

        var app = builder.Build();
        app.MapGrpcService<ScriptedNotificationGateway>();
        await app.StartAsync(cancellationToken);
        return new NotificationGatewayServer(app, gateway);
    }

    public GrpcNotificationSource CreateSource() => new(
        new bot::ContainerForest.Contracts.Bot.BotGateway.BotGatewayClient(channel),
        Options.Create(new BotOptions { ControllerUrl = new Uri("http://localhost:5081"), ServiceToken = InMemoryAggregator.ServiceToken }),
        TimeProvider.System,
        Logger);

    public async ValueTask DisposeAsync()
    {
        Gateway.Outgoing.Writer.TryComplete();
        channel.Dispose();
        await app.DisposeAsync();
    }
}

/// <summary>
/// WatchNotifications sends what the test writes to <see cref="Outgoing"/> and ends when it is completed,
/// or fails with <see cref="StreamFailure"/>. ReportDelivery records the request.
/// </summary>
internal sealed class ScriptedNotificationGateway : Proto.BotGateway.BotGatewayBase
{
    public Channel<Proto.Notification> Outgoing { get; } = Channel.CreateUnbounded<Proto.Notification>();

    public RpcException? StreamFailure { get; set; }

    public List<string?> ReceivedTokens { get; } = [];

    public List<Proto.ReportDeliveryRequest> Reports { get; } = [];

    public override async Task WatchNotifications(
        Proto.WatchNotificationsRequest request, IServerStreamWriter<Proto.Notification> responseStream, ServerCallContext context)
    {
        lock (ReceivedTokens)
        {
            ReceivedTokens.Add(context.RequestHeaders.GetValue(GrpcForestClient.BotTokenHeader));
        }

        if (StreamFailure is not null)
        {
            throw StreamFailure;
        }

        await foreach (var notification in Outgoing.Reader.ReadAllAsync(context.CancellationToken))
        {
            await responseStream.WriteAsync(notification, context.CancellationToken);
        }
    }

    public override Task<Proto.ReportDeliveryResponse> ReportDelivery(Proto.ReportDeliveryRequest request, ServerCallContext context)
    {
        lock (ReceivedTokens)
        {
            ReceivedTokens.Add(context.RequestHeaders.GetValue(GrpcForestClient.BotTokenHeader));
            Reports.Add(request);
        }

        return Task.FromResult(new Proto.ReportDeliveryResponse());
    }
}
