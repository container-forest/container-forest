extern alias bot;

using bot::ContainerForest.DiscordBot.Forest;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Proto = ContainerForest.Contracts.Bot;

namespace ContainerForest.DiscordBot.Tests.Forest;

public sealed class GrpcForestClientTests
{
    private const ulong DiscordUserId = 238_471_902_384_719_872;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListEnvironments_SendsTokenAndDiscordUser_AndMapsEnvironments()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Environments.Add(new Proto.Environment { Id = "0199a1b2-0000-7000-8000-000000000001", Name = "prod", Online = true });
        aggregator.Gateway.Environments.Add(new Proto.Environment { Id = "0199a1b2-0000-7000-8000-000000000002", Name = "lab", Online = false });

        var environments = await aggregator.CreateClient().ListEnvironmentsAsync(DiscordUserId, Ct);

        Assert.Equal(
            [
                new ForestEnvironment("0199a1b2-0000-7000-8000-000000000001", "prod", true),
                new ForestEnvironment("0199a1b2-0000-7000-8000-000000000002", "lab", false),
            ],
            environments);
        Assert.Equal([InMemoryAggregator.ServiceToken], aggregator.Gateway.ReceivedTokens);
        var request = Assert.IsType<Proto.ListEnvironmentsRequest>(Assert.Single(aggregator.Gateway.Requests));
        Assert.Equal(DiscordUserId, request.DiscordUserId);
    }

    [Fact]
    public async Task ListContainers_MapsContainers_AndLeavesStackEmptyOutsideCompose()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Containers.Add(new Proto.Container
        {
            Id = "4f1c2a9e8b7d",
            Name = "web-app-1",
            State = Proto.ContainerState.Running,
            Image = "ghcr.io/example/app:1.4.2",
            StackName = "web",
            ServiceName = "app",
            StatusText = "Up 3 minutes (healthy)",
            CanControl = true,
        });
        aggregator.Gateway.Containers.Add(new Proto.Container
        {
            Id = "9a8b7c6d5e4f",
            Name = "scratch",
            State = Proto.ContainerState.Exited,
            Image = "alpine:3.22",
            StatusText = "Exited (0) 2 hours ago",
        });

        var containers = await aggregator.CreateClient().ListContainersAsync(DiscordUserId, "env-1", Ct);

        Assert.Equal(
            [
                new ForestContainer(
                    "4f1c2a9e8b7d", "web-app-1", ContainerState.Running, "ghcr.io/example/app:1.4.2", "web", "app",
                    "Up 3 minutes (healthy)", CanControl: true),
                new ForestContainer(
                    "9a8b7c6d5e4f", "scratch", ContainerState.Exited, "alpine:3.22", null, null,
                    "Exited (0) 2 hours ago", CanControl: false),
            ],
            containers);
        var request = Assert.IsType<Proto.ListContainersRequest>(Assert.Single(aggregator.Gateway.Requests));
        Assert.Equal("env-1", request.EnvironmentId);
    }

    [Fact]
    public async Task ListContainers_MapsUnknownStateToUnknown()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Containers.Add(new Proto.Container { Id = "abc", Name = "new", State = (Proto.ContainerState)42 });

        var container = Assert.Single(await aggregator.CreateClient().ListContainersAsync(DiscordUserId, "env-1", Ct));

        Assert.Equal(ContainerState.Unknown, container.State);
    }

    [Theory]
    [InlineData(ContainerAction.Start, Proto.ContainerAction.Start, null)]
    [InlineData(ContainerAction.Stop, Proto.ContainerAction.Stop, 0)]
    [InlineData(ContainerAction.Restart, Proto.ContainerAction.Restart, 30)]
    public async Task ControlContainer_SendsActionContainerAndTimeout(
        ContainerAction action, Proto.ContainerAction expected, int? timeoutSeconds)
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);

        await aggregator.CreateClient().ControlContainerAsync(DiscordUserId, "env-1", "web-app-1", action, timeoutSeconds, Ct);

        var request = Assert.IsType<Proto.ControlContainerRequest>(Assert.Single(aggregator.Gateway.Requests));
        Assert.Equal(DiscordUserId, request.DiscordUserId);
        Assert.Equal("env-1", request.EnvironmentId);
        Assert.Equal("web-app-1", request.Container);
        Assert.Equal(expected, request.Action);
        Assert.Equal(timeoutSeconds.HasValue, request.HasTimeoutSeconds);
        Assert.Equal(timeoutSeconds ?? 0, request.TimeoutSeconds);
    }

    [Fact]
    public async Task GetCaller_ReturnsNullForUnlinkedAccount()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Caller = new Proto.GetCallerResponse { Linked = false };

        Assert.Null(await aggregator.CreateClient().GetCallerAsync(DiscordUserId, Ct));
    }

    [Fact]
    public async Task GetCaller_ReturnsLinkedUser()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Caller = new Proto.GetCallerResponse
        {
            Linked = true, UserId = "0199a1b2-0000-7000-8000-00000000000a", UserName = "dmytro", IsAdmin = false, IsActive = true,
        };

        var caller = await aggregator.CreateClient().GetCallerAsync(DiscordUserId, Ct);

        Assert.Equal(new ForestCaller("0199a1b2-0000-7000-8000-00000000000a", "dmytro", IsAdmin: false, IsActive: true), caller);
    }

    [Fact]
    public async Task LinkAccount_SendsTrimmedCodeAndDiscordName_AndReturnsUserName()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Link = new Proto.LinkAccountResponse { UserName = "dmytro" };

        var userName = await aggregator.CreateClient().LinkAccountAsync(DiscordUserId, "dmytro_s", "  K7Q-4M2  ", Ct);

        Assert.Equal("dmytro", userName);
        var request = Assert.IsType<Proto.LinkAccountRequest>(Assert.Single(aggregator.Gateway.Requests));
        Assert.Equal("K7Q-4M2", request.Code);
        Assert.Equal("dmytro_s", request.DiscordUserName);
        Assert.Equal(DiscordUserId, request.DiscordUserId);
    }

    [Theory]
    [InlineData(StatusCode.PermissionDenied, typeof(ForestAccessDeniedException))]
    [InlineData(StatusCode.NotFound, typeof(ForestNotFoundException))]
    [InlineData(StatusCode.InvalidArgument, typeof(ForestRequestRejectedException))]
    [InlineData(StatusCode.FailedPrecondition, typeof(ForestRequestRejectedException))]
    [InlineData(StatusCode.Unavailable, typeof(AgentUnavailableException))]
    [InlineData(StatusCode.DeadlineExceeded, typeof(AgentUnavailableException))]
    [InlineData(StatusCode.Unimplemented, typeof(AgentFailedException))]
    [InlineData(StatusCode.Internal, typeof(AgentFailedException))]
    public async Task AggregatorErrors_BecomeForestExceptions_WithTheAggregatorsMessage(StatusCode code, Type expected)
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Failure = ScriptedBotGateway.Fail(code, "Readable message from the aggregator.");

        var ex = await Assert.ThrowsAnyAsync<ForestException>(
            () => aggregator.CreateClient().ControlContainerAsync(DiscordUserId, "env-1", "web", ContainerAction.Restart, null, Ct));

        Assert.IsType(expected, ex);
        Assert.Equal("Readable message from the aggregator.", ex.Message);
        Assert.Empty(aggregator.Logger.Entries);
    }

    [Fact]
    public async Task AggregatorErrorWithoutMessage_GetsADefaultMessage()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Failure = ScriptedBotGateway.Fail(StatusCode.PermissionDenied, "");

        var ex = await Assert.ThrowsAsync<ForestAccessDeniedException>(
            () => aggregator.CreateClient().ListEnvironmentsAsync(DiscordUserId, Ct));

        Assert.Equal("You do not have permission to do that.", ex.Message);
    }

    [Fact]
    public async Task UnlinkedAccount_IsAccountNotLinked()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Failure = ScriptedBotGateway.Fail(
            StatusCode.Unauthenticated, "Discord account is not linked.", RpcErrorMapper.AccountNotLinkedReason);

        var ex = await Assert.ThrowsAsync<AccountNotLinkedException>(
            () => aggregator.CreateClient().ListEnvironmentsAsync(DiscordUserId, Ct));

        Assert.Contains("/link", ex.Message);
        Assert.Empty(aggregator.Logger.Entries);
    }

    [Fact]
    public async Task InvalidBotToken_IsLoggedForTheOperator_AndHiddenFromTheUser()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Failure = ScriptedBotGateway.Fail(
            StatusCode.Unauthenticated, "Invalid bot token.", RpcErrorMapper.InvalidBotTokenReason);

        var ex = await Assert.ThrowsAsync<ForestUnavailableException>(
            () => aggregator.CreateClient().ListEnvironmentsAsync(DiscordUserId, Ct));

        Assert.Equal("The bot is temporarily unavailable. Try again later.", ex.Message);
        var entry = Assert.Single(aggregator.Logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("CF_BOT__SERVICETOKEN", entry.Message);
        Assert.DoesNotContain(InMemoryAggregator.ServiceToken, entry.Message);
        Assert.DoesNotContain(InMemoryAggregator.ServiceToken, ex.ToString());
    }

    [Fact]
    public async Task UnauthenticatedWithoutKnownReason_IsTreatedAsAConfigurationError()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Failure = ScriptedBotGateway.Fail(StatusCode.Unauthenticated, "No.");

        await Assert.ThrowsAsync<ForestUnavailableException>(() => aggregator.CreateClient().ListEnvironmentsAsync(DiscordUserId, Ct));

        Assert.Equal(LogLevel.Error, Assert.Single(aggregator.Logger.Entries).Level);
    }

    public static TheoryData<Exception> ConnectionFailures() =>
    [
        // A refused connection; the client reports it as Unavailable, the same code the aggregator uses for an offline agent.
        new HttpRequestException("Connection refused (localhost:5081)", new SocketException((int)SocketError.ConnectionRefused)),
        // Other transport failures; the client reports them as Internal, the same code as an agent error.
        new HttpRequestException("The SSL connection could not be established."),
    ];

    [Theory]
    [MemberData(nameof(ConnectionFailures))]
    public async Task UnreachableAggregator_IsLoggedForTheOperator_AndNotBlamedOnTheAgent(Exception failure)
    {
        using var channel = GrpcChannel.ForAddress(
            "http://localhost:5081", new GrpcChannelOptions { HttpHandler = new FailingHandler(failure) });
        var logger = new RecordingLogger<bot::ContainerForest.DiscordBot.Forest.GrpcForestClient>();
        var client = InMemoryAggregator.CreateClient(channel, logger);

        var ex = await Assert.ThrowsAsync<ForestUnavailableException>(() => client.ListEnvironmentsAsync(DiscordUserId, Ct));

        Assert.Equal("The bot is temporarily unavailable. Try again later.", ex.Message);
        Assert.Contains("unreachable", Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public async Task DeadlineWithoutMessage_TellsTheUserToCheckBeforeRetrying()
    {
        // What the client reports when its own deadline passes. A real deadline cannot be tested against TestServer:
        // the server enforces the propagated deadline too and resets the stream before the client's timer fires.
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Failure = ScriptedBotGateway.Fail(StatusCode.DeadlineExceeded, "");

        var ex = await Assert.ThrowsAsync<AgentUnavailableException>(
            () => aggregator.CreateClient().ListEnvironmentsAsync(DiscordUserId, Ct));

        Assert.Contains("check", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_IsOperationCanceled()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Hold = new TaskCompletionSource();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => aggregator.CreateClient().ListEnvironmentsAsync(DiscordUserId, cts.Token));
        Assert.Empty(aggregator.Logger.Entries);
    }
}
