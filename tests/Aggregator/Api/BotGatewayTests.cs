using System.Net;
using System.Net.Http.Json;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Contracts.Agent;
using ContainerForest.Contracts.Bot;
using Grpc.Core;
using AgentContainerState = ContainerForest.Contracts.Agent.ContainerState;
using BotContainerState = ContainerForest.Contracts.Bot.ContainerState;
using DeploymentStatus = ContainerForest.Contracts.Bot.DeploymentStatus;

namespace ContainerForest.Aggregator.Api.Tests;

public class BotGatewayTests
{
    private const ulong AliceDiscordId = 111_222_333_444_555_666;
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

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

    [Fact]
    public async Task Linked_admin_lists_stacks_and_restarts_one_through_the_agent()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        await LinkAsync(app, AggregatorApp.AdminUserName, AliceDiscordId);
        await CreateStackAsync(app, agent, "web");
        var environmentId = agent.EnvironmentId.ToString();

        var stacks = await app.BotGateway.ListStacksAsync(
            new ListStacksRequest { DiscordUserId = AliceDiscordId, EnvironmentId = environmentId }, BotHeaders(), cancellationToken: Ct);
        var stack = Assert.Single(stacks.Stacks);
        Assert.Equal(("web", false, "", true, true), (stack.Name, stack.IsSystem, stack.DeployedCommit, stack.CanRestart, stack.CanTakeDown));

        var restart = app.BotGateway.ControlStackAsync(
            new ControlStackRequest
            {
                DiscordUserId = AliceDiscordId, EnvironmentId = environmentId, Stack = "web", Action = StackAction.Restart, TimeoutSeconds = 0,
            },
            BotHeaders(),
            cancellationToken: Ct);
        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.RestartStack, command.BodyCase);
        Assert.Equal(("web", 0), (command.RestartStack.StackName, command.RestartStack.TimeoutSeconds));
        await agent.SendAsync(new AgentMessage { Progress = new CommandProgress { RequestId = command.RequestId, Line = "Container web-app-1 Started" } });
        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = command.RequestId,
                Error = new CommandError { Code = ErrorCode.CommandFailed, Message = "docker compose restart exited with code 1; see the command output." },
            },
        });

        var response = await restart;
        Assert.Equal("docker compose restart exited with code 1; see the command output.", response.Error);
        Assert.Equal(["Container web-app-1 Started"], response.Output);
    }

    [Fact]
    public async Task Viewer_may_not_restart_a_stack_and_unknown_stacks_are_not_found()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        await using var agent = await app.EnrollAndConnectAsync();
        await LinkAsync(app, "alice", AliceDiscordId);
        await CreateStackAsync(app, agent, "web");
        var environmentId = agent.EnvironmentId.ToString();

        var stacks = await app.BotGateway.ListStacksAsync(
            new ListStacksRequest { DiscordUserId = AliceDiscordId, EnvironmentId = environmentId }, BotHeaders(), cancellationToken: Ct);
        var stack = Assert.Single(stacks.Stacks);
        Assert.False(stack.CanRestart);
        Assert.False(stack.CanTakeDown);

        Assert.Equal(StatusCode.PermissionDenied, (await ControlStackFailsAsync(app, environmentId, "web", StackAction.Down)).StatusCode);
        Assert.Equal(StatusCode.NotFound, (await ControlStackFailsAsync(app, environmentId, "nope", StackAction.Restart)).StatusCode);
        Assert.Equal(StatusCode.NotFound, (await ControlStackFailsAsync(app, environmentId, "Not a name!", StackAction.Restart)).StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, (await ControlStackFailsAsync(app, environmentId, "web", StackAction.Unspecified)).StatusCode);
    }

    [Fact]
    public async Task Linked_admin_deploys_a_stack_and_follows_the_deployment()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        await LinkAsync(app, AggregatorApp.AdminUserName, AliceDiscordId);
        await CreateStackAsync(app, agent, "web");
        app.Git.Snapshot = new GitSnapshot(
            CommitSha.Parse(Sha),
            [new SourceFile(StackFilePath.Parse("compose.yaml"), "services:\n  web:\n    image: nginx:1.27\n"u8.ToArray(), IsExecutable: false)],
            []);
        var environmentId = agent.EnvironmentId.ToString();

        var stacks = await app.BotGateway.ListStacksAsync(
            new ListStacksRequest { DiscordUserId = AliceDiscordId, EnvironmentId = environmentId }, BotHeaders(), cancellationToken: Ct);
        Assert.True(Assert.Single(stacks.Stacks).CanDeploy);

        var queued = await app.BotGateway.DeployStackAsync(
            new DeployStackRequest { DiscordUserId = AliceDiscordId, EnvironmentId = environmentId, Stack = " web ", Pull = true },
            BotHeaders(),
            cancellationToken: Ct);
        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.DeployStack, command.BodyCase);
        Assert.Equal(("web", PullPolicy.Always), (command.DeployStack.StackName, command.DeployStack.Pull));

        // One deployment per stack at a time, as in the panel.
        var second = await DeployStackFailsAsync(app, environmentId, "web");
        Assert.Equal(StatusCode.FailedPrecondition, second.StatusCode);

        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });

        GetDeploymentResponse? deployment = null;
        for (var attempt = 0; attempt < 100 && deployment?.Status != DeploymentStatus.Succeeded; attempt++)
        {
            await Task.Delay(50, Ct);
            deployment = await app.BotGateway.GetDeploymentAsync(
                new GetDeploymentRequest { DiscordUserId = AliceDiscordId, DeploymentId = queued.DeploymentId }, BotHeaders(), cancellationToken: Ct);
        }

        Assert.Equal(DeploymentStatus.Succeeded, deployment!.Status);
        Assert.Equal((Sha, ""), (deployment.Commit, deployment.Error));
        Assert.True(deployment.FinishedUnixMs >= deployment.QueuedUnixMs);
    }

    [Fact]
    public async Task Viewer_may_not_deploy_and_unknown_stacks_and_deployments_are_not_found()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        await using var agent = await app.EnrollAndConnectAsync();
        await LinkAsync(app, "alice", AliceDiscordId);
        await CreateStackAsync(app, agent, "web");
        var environmentId = agent.EnvironmentId.ToString();

        var stacks = await app.BotGateway.ListStacksAsync(
            new ListStacksRequest { DiscordUserId = AliceDiscordId, EnvironmentId = environmentId }, BotHeaders(), cancellationToken: Ct);
        Assert.False(Assert.Single(stacks.Stacks).CanDeploy);

        Assert.Equal(StatusCode.PermissionDenied, (await DeployStackFailsAsync(app, environmentId, "web")).StatusCode);
        Assert.Equal(StatusCode.NotFound, (await DeployStackFailsAsync(app, environmentId, "nope")).StatusCode);
        Assert.Equal(StatusCode.NotFound, (await GetDeploymentFailsAsync(app, Guid.NewGuid().ToString())).StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, (await GetDeploymentFailsAsync(app, "not-an-id")).StatusCode);
    }

    private static Task<RpcException> DeployStackFailsAsync(AggregatorApp app, string environmentId, string stack) =>
        Assert.ThrowsAsync<RpcException>(() => app.BotGateway.DeployStackAsync(
            new DeployStackRequest { DiscordUserId = AliceDiscordId, EnvironmentId = environmentId, Stack = stack },
            BotHeaders(),
            cancellationToken: Ct).ResponseAsync);

    private static Task<RpcException> GetDeploymentFailsAsync(AggregatorApp app, string deploymentId) =>
        Assert.ThrowsAsync<RpcException>(() => app.BotGateway.GetDeploymentAsync(
            new GetDeploymentRequest { DiscordUserId = AliceDiscordId, DeploymentId = deploymentId },
            BotHeaders(),
            cancellationToken: Ct).ResponseAsync);

    private static Task<RpcException> ControlStackFailsAsync(AggregatorApp app, string environmentId, string stack, StackAction action) =>
        Assert.ThrowsAsync<RpcException>(() => app.BotGateway.ControlStackAsync(
            new ControlStackRequest { DiscordUserId = AliceDiscordId, EnvironmentId = environmentId, Stack = stack, Action = action },
            BotHeaders(),
            cancellationToken: Ct).ResponseAsync);

    private static async Task CreateStackAsync(AggregatorApp app, FakeAgent agent, string name)
    {
        var client = app.CreateClient();
        var repository = await client.PostAsJsonAsync(
            "/api/repositories", new { name = "infra", url = "https://git.example.com/infra.git" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, repository);
        var repositoryId = (await repository.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;

        await HttpAssert.StatusAsync(
            HttpStatusCode.Created,
            client.PostAsJsonAsync(
                $"/api/environments/{agent.EnvironmentId}/stacks",
                new { name, repositoryId, branch = "main", composePath = "compose.yaml" },
                Ct));
    }

    private sealed record IdView(Guid Id);

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
