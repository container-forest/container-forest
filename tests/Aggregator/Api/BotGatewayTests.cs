using System.Net;
using System.Net.Http.Json;
using ContainerForest.Contracts.Agent;
using ContainerForest.Contracts.Bot;
using Grpc.Core;
using AgentContainerState = ContainerForest.Contracts.Agent.ContainerState;
using BotContainerState = ContainerForest.Contracts.Bot.ContainerState;

namespace ContainerForest.Aggregator.Api.Tests;

public class BotGatewayTests
{
    private const ulong AliceDiscordId = 111_222_333_444_555_666;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Metadata BotHeaders(string token = AggregatorApp.BotToken) => new() { { "x-bot-token", token } };

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Calls_without_the_bot_token_are_rejected(string? token)
    {
        await using var app = new AggregatorApp();
        var headers = token is null ? new Metadata() : BotHeaders(token);

        var error = await Assert.ThrowsAsync<RpcException>(() =>
            app.BotGateway.GetCallerAsync(new GetCallerRequest { DiscordUserId = AliceDiscordId }, headers, cancellationToken: Ct).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        Assert.Equal("invalid-bot-token", error.Trailers.GetValue("x-cf-reason"));
    }

    [Fact]
    public async Task Unlinked_account_is_reported_by_GetCaller_and_rejected_elsewhere()
    {
        await using var app = new AggregatorApp();

        var caller = await app.BotGateway.GetCallerAsync(
            new GetCallerRequest { DiscordUserId = AliceDiscordId }, BotHeaders(), cancellationToken: Ct);
        var error = await Assert.ThrowsAsync<RpcException>(() => app.BotGateway.ListEnvironmentsAsync(
            new ListEnvironmentsRequest { DiscordUserId = AliceDiscordId }, BotHeaders(), cancellationToken: Ct).ResponseAsync);

        Assert.False(caller.Linked);
        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        Assert.Equal("account-not-linked", error.Trailers.GetValue("x-cf-reason"));
    }

    [Fact]
    public async Task Code_from_the_panel_links_the_account()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);

        await LinkAsync(app, "alice", AliceDiscordId);

        var caller = await app.BotGateway.GetCallerAsync(
            new GetCallerRequest { DiscordUserId = AliceDiscordId }, BotHeaders(), cancellationToken: Ct);
        Assert.True(caller.Linked);
        Assert.Equal("alice", caller.UserName);
        var status = await app.CreateClientAs("alice").GetFromJsonAsync<LinkStatus>("/api/me/discord", AggregatorApp.Json, Ct);
        Assert.True(status!.Linked);
        Assert.Equal("alice#0001", status.DiscordUserName);
    }

    [Fact]
    public async Task Wrong_code_is_an_invalid_argument()
    {
        await using var app = new AggregatorApp();

        var error = await Assert.ThrowsAsync<RpcException>(() => app.BotGateway.LinkAccountAsync(
            new LinkAccountRequest { DiscordUserId = AliceDiscordId, DiscordUserName = "alice", Code = "ZZZZZZZZ" },
            BotHeaders(),
            cancellationToken: Ct).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task Linked_admin_restarts_a_container_through_the_agent()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        await LinkAsync(app, AggregatorApp.AdminUserName, AliceDiscordId);

        var environments = await app.BotGateway.ListEnvironmentsAsync(
            new ListEnvironmentsRequest { DiscordUserId = AliceDiscordId }, BotHeaders(), cancellationToken: Ct);
        var environment = Assert.Single(environments.Environments);
        Assert.True(environment.Online);

        var list = app.BotGateway.ListContainersAsync(
            new ListContainersRequest { DiscordUserId = AliceDiscordId, EnvironmentId = environment.Id }, BotHeaders(), cancellationToken: Ct);
        await AnswerListAsync(agent);
        var container = Assert.Single((await list).Containers);
        Assert.Equal(BotContainerState.Running, container.State);
        Assert.True(container.CanControl);

        var restart = app.BotGateway.ControlContainerAsync(
            new ControlContainerRequest
            {
                DiscordUserId = AliceDiscordId,
                EnvironmentId = environment.Id,
                Container = "web-app-1",
                Action = ContainerAction.Restart,
                TimeoutSeconds = 5,
            },
            BotHeaders(),
            cancellationToken: Ct);
        await AnswerListAsync(agent);
        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.RestartContainer, command.BodyCase);
        Assert.Equal(5, command.RestartContainer.TimeoutSeconds);
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });
        await restart;
    }

    [Fact]
    public async Task Viewer_is_denied_and_errors_map_to_status_codes()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        await using var agent = await app.EnrollAndConnectAsync();
        await LinkAsync(app, "alice", AliceDiscordId);

        // Public environments give an implicit Viewer role: containers are visible, not controllable.
        var stop = app.BotGateway.ControlContainerAsync(
            new ControlContainerRequest
            {
                DiscordUserId = AliceDiscordId, EnvironmentId = agent.EnvironmentId.ToString(), Container = "c1", Action = ContainerAction.Stop,
            },
            BotHeaders(),
            cancellationToken: Ct);
        await AnswerListAsync(agent);
        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(() => stop.ResponseAsync)).StatusCode);

        var unknown = await Assert.ThrowsAsync<RpcException>(() => app.BotGateway.ListContainersAsync(
            new ListContainersRequest { DiscordUserId = AliceDiscordId, EnvironmentId = Guid.NewGuid().ToString() },
            BotHeaders(),
            cancellationToken: Ct).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);

        var malformed = await Assert.ThrowsAsync<RpcException>(() => app.BotGateway.ListContainersAsync(
            new ListContainersRequest { DiscordUserId = AliceDiscordId, EnvironmentId = "prod" },
            BotHeaders(),
            cancellationToken: Ct).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, malformed.StatusCode);
    }

    private static async Task LinkAsync(AggregatorApp app, string userName, ulong discordUserId)
    {
        var response = await app.CreateClientAs(userName).PostAsync("/api/me/discord/link-code", content: null, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, response);
        var code = (await response.Content.ReadFromJsonAsync<LinkCode>(AggregatorApp.Json, Ct))!.Code;

        var linked = await app.BotGateway.LinkAccountAsync(
            new LinkAccountRequest { DiscordUserId = discordUserId, DiscordUserName = $"{userName}#0001", Code = code.ToLowerInvariant() },
            BotHeaders(),
            cancellationToken: Ct);
        Assert.Equal(userName, linked.UserName);
    }

    private static async Task AnswerListAsync(FakeAgent agent)
    {
        var list = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.ListContainers, list.BodyCase);
        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = list.RequestId,
                Containers = new ContainerList
                {
                    Items = { new ContainerInfo { Id = "c1", Name = "web-app-1", State = AgentContainerState.Running, Image = "nginx:1.27" } },
                },
            },
        });
    }

    private sealed record LinkCode(string Code, DateTimeOffset ExpiresAt);

    private sealed record LinkStatus(bool Linked, string? DiscordUserName);
}
