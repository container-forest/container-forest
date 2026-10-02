extern alias bot;

using bot::ContainerForest.DiscordBot.Forest;
using Grpc.Core;
using Proto = ContainerForest.Contracts.Bot;

namespace ContainerForest.DiscordBot.Tests.Forest;

public sealed class GrpcForestClientStackTests
{
    private const ulong DiscordUserId = 238_471_902_384_719_872;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListStacks_MapsStacks_AndLeavesNeverDeployedOnesWithoutCommit()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Stacks.Add(new Proto.Stack
        {
            Id = "s1",
            Name = "web",
            DeployedCommit = "0123456789abcdef0123456789abcdef01234567",
            DeployedUnixMs = 1_790_000_000_000,
            CanRestart = true,
        });
        aggregator.Gateway.Stacks.Add(new Proto.Stack { Id = "s2", Name = "forest", IsSystem = true });

        var stacks = await aggregator.CreateClient().ListStacksAsync(DiscordUserId, "env-1", Ct);

        Assert.Equal(
            [
                new ForestStack(
                    "s1", "web", IsSystem: false, "0123456789abcdef0123456789abcdef01234567",
                    DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000), CanRestart: true, CanTakeDown: false),
                new ForestStack("s2", "forest", IsSystem: true, null, null, CanRestart: false, CanTakeDown: false),
            ],
            stacks);
        var request = Assert.IsType<Proto.ListStacksRequest>(Assert.Single(aggregator.Gateway.Requests));
        Assert.Equal(("env-1", DiscordUserId), (request.EnvironmentId, request.DiscordUserId));
    }

    [Theory]
    [InlineData(StackAction.Restart, Proto.StackAction.Restart)]
    [InlineData(StackAction.Down, Proto.StackAction.Down)]
    public async Task ControlStack_SendsTheAction_AndReturnsTheOutput(StackAction action, Proto.StackAction expected)
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.StackResponse = new Proto.ControlStackResponse { Output = { "Container web-app-1 Started" } };

        var result = await aggregator.CreateClient().ControlStackAsync(DiscordUserId, "env-1", "web", action, 0, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(["Container web-app-1 Started"], result.Output);
        var request = Assert.IsType<Proto.ControlStackRequest>(Assert.Single(aggregator.Gateway.Requests));
        Assert.Equal(expected, request.Action);
        Assert.Equal("web", request.Stack);
        Assert.True(request.HasTimeoutSeconds);
        Assert.Equal(0, request.TimeoutSeconds);
    }

    [Fact]
    public async Task ControlStack_WithoutTimeout_LeavesItUnset_AndReportsAComposeError()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.StackResponse = new Proto.ControlStackResponse { Error = "docker compose down exited with code 1" };

        var result = await aggregator.CreateClient().ControlStackAsync(DiscordUserId, "env-1", "web", StackAction.Down, null, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("docker compose down exited with code 1", result.Error);
        Assert.False(Assert.IsType<Proto.ControlStackRequest>(Assert.Single(aggregator.Gateway.Requests)).HasTimeoutSeconds);
    }

    [Fact]
    public async Task ControlStack_StatusBecomesAForestException()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Failure = ScriptedBotGateway.Fail(StatusCode.FailedPrecondition, "Stack 'web' has a deployment in progress.");

        var error = await Assert.ThrowsAsync<ForestRequestRejectedException>(() =>
            aggregator.CreateClient().ControlStackAsync(DiscordUserId, "env-1", "web", StackAction.Restart, null, Ct));
        Assert.Equal("Stack 'web' has a deployment in progress.", error.Message);
    }

    [Fact]
    public async Task ListStacks_PassesCanDeploy()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Stacks.Add(new Proto.Stack { Id = "s1", Name = "web", CanDeploy = true });

        var stack = Assert.Single(await aggregator.CreateClient().ListStacksAsync(DiscordUserId, "env-1", Ct));

        Assert.True(stack.CanDeploy);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeployStack_SendsTheStackAndPull_AndReturnsTheDeploymentId(bool pull)
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.DeployResponse = new Proto.DeployStackResponse { DeploymentId = "dep-1" };

        var id = await aggregator.CreateClient().DeployStackAsync(DiscordUserId, "env-1", "web", pull, Ct);

        Assert.Equal("dep-1", id);
        var request = Assert.IsType<Proto.DeployStackRequest>(Assert.Single(aggregator.Gateway.Requests));
        Assert.Equal((DiscordUserId, "env-1", "web", pull), (request.DiscordUserId, request.EnvironmentId, request.Stack, request.Pull));
    }

    [Fact]
    public async Task DeployStack_RefusedForAnOfflineAgent_BecomesAgentUnavailable()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.Failure = ScriptedBotGateway.Fail(StatusCode.Unavailable, "The agent of environment 'prod' is offline.");

        var error = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            aggregator.CreateClient().DeployStackAsync(DiscordUserId, "env-1", "web", pull: false, Ct));
        Assert.Equal("The agent of environment 'prod' is offline.", error.Message);
    }

    [Fact]
    public async Task GetDeployment_MapsAFinishedDeployment()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.DeploymentResponse = new Proto.GetDeploymentResponse
        {
            Status = Proto.DeploymentStatus.Failed,
            Commit = "0123456789abcdef0123456789abcdef01234567",
            Error = "compose failed",
            QueuedUnixMs = 1_790_000_000_000,
            FinishedUnixMs = 1_790_000_060_000,
        };

        var deployment = await aggregator.CreateClient().GetDeploymentAsync(DiscordUserId, "dep-1", Ct);

        Assert.Equal(
            new ForestDeployment(
                DeploymentStatus.Failed,
                "0123456789abcdef0123456789abcdef01234567",
                "compose failed",
                DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000),
                DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_060_000)),
            deployment);
        Assert.True(deployment.IsFinished);
        Assert.Equal("dep-1", Assert.IsType<Proto.GetDeploymentRequest>(Assert.Single(aggregator.Gateway.Requests)).DeploymentId);
    }

    [Fact]
    public async Task GetDeployment_LeavesWhatIsNotThereYetEmpty()
    {
        await using var aggregator = await InMemoryAggregator.StartAsync(Ct);
        aggregator.Gateway.DeploymentResponse = new Proto.GetDeploymentResponse { Status = Proto.DeploymentStatus.Queued, QueuedUnixMs = 1 };

        var deployment = await aggregator.CreateClient().GetDeploymentAsync(DiscordUserId, "dep-1", Ct);

        Assert.Equal((DeploymentStatus.Queued, null, null, null), (deployment.Status, deployment.Commit, deployment.Error, deployment.FinishedAt));
        Assert.False(deployment.IsFinished);
    }
}
