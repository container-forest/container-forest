extern alias bot;

using bot::ContainerForest.DiscordBot.Configuration;
using bot::ContainerForest.DiscordBot.Forest;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Proto = ContainerForest.Contracts.Bot;

namespace ContainerForest.DiscordBot.Tests.Forest;

/// <summary>A BotGateway server in memory with scripted answers, and a real gRPC client connected to it.</summary>
internal sealed class InMemoryAggregator : IAsyncDisposable
{
    public const string ServiceToken = "cf-test-token-3d9e1b7c";

    private readonly WebApplication app;
    private readonly GrpcChannel channel;

    private InMemoryAggregator(WebApplication app, ScriptedBotGateway gateway)
    {
        this.app = app;
        Gateway = gateway;
        var server = app.GetTestServer();
        channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions { HttpHandler = server.CreateHandler() });
    }

    public ScriptedBotGateway Gateway { get; }

    public RecordingLogger<GrpcForestClient> Logger { get; } = new();

    public static async Task<InMemoryAggregator> StartAsync(CancellationToken cancellationToken)
    {
        var gateway = new ScriptedBotGateway();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(gateway);

        var app = builder.Build();
        app.MapGrpcService<ScriptedBotGateway>();
        await app.StartAsync(cancellationToken);
        return new InMemoryAggregator(app, gateway);
    }

    public GrpcForestClient CreateClient() => CreateClient(channel, Logger);

    /// <summary>A client whose calls go through <paramref name="channel"/> instead of this server.</summary>
    public static GrpcForestClient CreateClient(GrpcChannel channel, ILogger<GrpcForestClient> logger)
    {
        var options = new BotOptions { ControllerUrl = new Uri("http://localhost:5081"), ServiceToken = ServiceToken };
        return new GrpcForestClient(
            new bot::ContainerForest.Contracts.Bot.BotGateway.BotGatewayClient(channel),
            Options.Create(options),
            TimeProvider.System,
            logger);
    }

    public async ValueTask DisposeAsync()
    {
        Gateway.Hold?.TrySetResult();
        channel.Dispose();
        await app.DisposeAsync();
    }
}

/// <summary>Answers every call with the configured response, or throws <see cref="Failure"/> when it is set.</summary>
internal sealed class ScriptedBotGateway : Proto.BotGateway.BotGatewayBase
{
    public List<string?> ReceivedTokens { get; } = [];

    public List<IMessage> Requests { get; } = [];

    public RpcException? Failure { get; set; }

    /// <summary>When set, calls wait for it, ignoring their own cancellation. Completed when the server stops.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public Proto.GetCallerResponse Caller { get; set; } = new();

    public Proto.LinkAccountResponse Link { get; set; } = new();

    public List<Proto.Environment> Environments { get; } = [];

    public List<Proto.Container> Containers { get; } = [];

    public List<Proto.Stack> Stacks { get; } = [];

    public Proto.ControlStackResponse StackResponse { get; set; } = new();

    public static RpcException Fail(StatusCode code, string detail, string? reason = null)
    {
        var trailers = new Metadata();
        if (reason is not null)
        {
            trailers.Add(RpcErrorMapper.ReasonTrailer, reason);
        }

        return new RpcException(new Status(code, detail), trailers);
    }

    public override Task<Proto.GetCallerResponse> GetCaller(Proto.GetCallerRequest request, ServerCallContext context) =>
        AnswerAsync(request, context, Caller);

    public override Task<Proto.LinkAccountResponse> LinkAccount(Proto.LinkAccountRequest request, ServerCallContext context) =>
        AnswerAsync(request, context, Link);

    public override Task<Proto.ListEnvironmentsResponse> ListEnvironments(
        Proto.ListEnvironmentsRequest request, ServerCallContext context) =>
        AnswerAsync(request, context, new Proto.ListEnvironmentsResponse { Environments = { Environments } });

    public override Task<Proto.ListContainersResponse> ListContainers(
        Proto.ListContainersRequest request, ServerCallContext context) =>
        AnswerAsync(request, context, new Proto.ListContainersResponse { Containers = { Containers } });

    public override Task<Proto.ControlContainerResponse> ControlContainer(
        Proto.ControlContainerRequest request, ServerCallContext context) =>
        AnswerAsync(request, context, new Proto.ControlContainerResponse());

    public override Task<Proto.ListStacksResponse> ListStacks(Proto.ListStacksRequest request, ServerCallContext context) =>
        AnswerAsync(request, context, new Proto.ListStacksResponse { Stacks = { Stacks } });

    public override Task<Proto.ControlStackResponse> ControlStack(Proto.ControlStackRequest request, ServerCallContext context) =>
        AnswerAsync(request, context, StackResponse);

    private async Task<T> AnswerAsync<T>(IMessage request, ServerCallContext context, T response)
    {
        lock (Requests)
        {
            ReceivedTokens.Add(context.RequestHeaders.GetValue(GrpcForestClient.BotTokenHeader));
            Requests.Add(request);
        }

        if (Hold is not null)
        {
            // TestServer notices the client's cancellation only once the response has started.
            await context.WriteResponseHeadersAsync([]);
            await Hold.Task;
        }

        return Failure is null ? response : throw Failure;
    }
}

/// <summary>Keeps formatted log messages so tests can check what the bot logs, and what it must not.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
        }
    }
}

/// <summary>Fails every request with <paramref name="failure"/>, as a refused connection or a broken proxy does.</summary>
internal sealed class FailingHandler(Exception failure) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(failure);
}
