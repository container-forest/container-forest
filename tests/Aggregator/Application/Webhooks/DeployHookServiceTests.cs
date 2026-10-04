using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Webhooks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using Microsoft.Extensions.Logging.Abstractions;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Webhooks;

public class DeployHookServiceTests
{
    private const string Token = "hook-token";

    private readonly InMemoryStacks stacks = new();
    private readonly InMemoryDeployments deployments = new();
    private readonly FakeAgentChannel agents = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly FakeDeploymentQueue queue = new();
    private readonly PushDeploymentGate gate = new();
    private readonly Stack web;
    private readonly DeployHookService service;

    public DeployHookServiceTests()
    {
        web = AddStack("web", Token);
        service = new DeployHookService(
            stacks, deployments, agents, unitOfWork, queue, gate, new FixedTime(Now), NullLogger<DeployHookService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Call_queues_a_deployment_that_pulls_every_image()
    {
        var result = await service.ReceiveAsync(web.Id, Token, Ct);

        var deployment = Assert.Single(deployments.All);
        Assert.Equal((DeployHookOutcome.Queued, deployment.Id), (result.Outcome, result.DeploymentId));
        Assert.Equal(
            (DeploymentTrigger.DeployHook, ImagePullPolicy.Always, null, false),
            (deployment.Trigger, deployment.PullPolicy, deployment.RequestedBy, deployment.IsPinned));
        Assert.Equal([deployment.Id], queue.Queued);
    }

    [Fact]
    public async Task Wrong_or_missing_token_unknown_stack_and_stack_without_hook_are_rejected_alike()
    {
        var withoutHook = AddStack("api", token: null);

        await Assert.ThrowsAsync<WebhookRejectedException>(() => service.ReceiveAsync(web.Id, "guess", Ct));
        await Assert.ThrowsAsync<WebhookRejectedException>(() => service.ReceiveAsync(web.Id, null, Ct));
        await Assert.ThrowsAsync<WebhookRejectedException>(() => service.ReceiveAsync(StackId.New(), Token, Ct));
        await Assert.ThrowsAsync<WebhookRejectedException>(() => service.ReceiveAsync(withoutHook.Id, Token, Ct));
        Assert.Empty(deployments.All);
    }

    [Fact]
    public async Task Offline_agent_gets_nothing_queued()
    {
        agents.Online = false;

        await Assert.ThrowsAsync<AgentOfflineException>(() => service.ReceiveAsync(web.Id, Token, Ct));
        Assert.Empty(deployments.All);
    }

    [Fact]
    public async Task Queued_deployment_that_pulls_everything_takes_the_call()
    {
        var queued = Deployment.QueueManual(web.Id, UserId.New(), ImagePullPolicy.Always, Now);
        deployments.Add(queued);

        var result = await service.ReceiveAsync(web.Id, Token, Ct);

        Assert.Equal(DeployHookOutcome.AlreadyQueued, result.Outcome);
        Assert.Single(deployments.All);
    }

    [Fact]
    public async Task Call_during_a_deployment_deploys_with_fresh_images_after_it()
    {
        var running = Deployment.QueueManual(web.Id, UserId.New(), ImagePullPolicy.Always, Now);
        running.Start(Now);
        deployments.Add(running);

        var result = await service.ReceiveAsync(web.Id, Token, Ct);
        running.ResolveCommit(CommitSha.Parse(new string('a', 40)));
        running.Succeed(Now);
        await FollowUp().DeploymentFinishedAsync(Outcome(running), Ct);

        Assert.Equal(DeployHookOutcome.AfterRunning, result.Outcome);
        var followUp = Assert.Single(deployments.All, d => d.Id != running.Id);
        Assert.Equal((DeploymentTrigger.DeployHook, ImagePullPolicy.Always), (followUp.Trigger, followUp.PullPolicy));
    }

    [Fact]
    public async Task Queued_deployment_without_pull_is_followed_by_one_that_pulls()
    {
        var queued = Deployment.QueueFromWebhook(web.Id, Now);
        deployments.Add(queued);

        var result = await service.ReceiveAsync(web.Id, Token, Ct);
        queued.Start(Now);
        queued.Fail("boom", Now);
        await FollowUp().DeploymentFinishedAsync(Outcome(queued), Ct);

        Assert.Equal(DeployHookOutcome.AfterRunning, result.Outcome);
        Assert.Equal(DeploymentTrigger.DeployHook, Assert.Single(deployments.All, d => d.Id != queued.Id).Trigger);
    }

    [Fact]
    public async Task Hook_waits_behind_a_deployment_queued_in_between_that_does_not_pull()
    {
        var running = Deployment.QueueManual(web.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        running.Start(Now);
        deployments.Add(running);
        await service.ReceiveAsync(web.Id, Token, Ct);

        // Someone deploys without pulling before the first one finishes.
        running.Fail("boom", Now);
        var manual = Deployment.QueueManual(web.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        deployments.Add(manual);
        await FollowUp().DeploymentFinishedAsync(Outcome(running), Ct);
        Assert.Equal(2, deployments.All.Count);

        manual.Start(Now);
        manual.Fail("boom", Now);
        await FollowUp().DeploymentFinishedAsync(Outcome(manual), Ct);

        Assert.Equal(DeploymentTrigger.DeployHook, deployments.All.Single(d => d.Id != running.Id && d.Id != manual.Id).Trigger);
    }

    [Fact]
    public async Task Push_after_the_hook_does_not_drop_its_pull()
    {
        var running = Deployment.QueueManual(web.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        running.Start(Now);
        deployments.Add(running);

        await service.ReceiveAsync(web.Id, Token, Ct);
        using (await gate.EnterAsync(Ct))
        {
            gate.MarkPending(web.Id, ImagePullPolicy.Missing);
        }

        running.Fail("boom", Now);
        await FollowUp().DeploymentFinishedAsync(Outcome(running), Ct);

        Assert.Equal(ImagePullPolicy.Always, deployments.All.Single(d => d.Id != running.Id).PullPolicy);
    }

    private PushFollowUpObserver FollowUp() =>
        new(gate, deployments, agents, unitOfWork, queue, new FixedTime(Now), NullLogger<PushFollowUpObserver>.Instance);

    private DeploymentOutcome Outcome(Deployment deployment) =>
        new(deployment, web, DockerEnvironment.Create("prod", isPrivate: false, Now));

    private Stack AddStack(string name, string? token)
    {
        var stack = Stack.Create(
            EnvironmentId.New(), StackName.Parse(name), RepositoryId.New(), BranchName.Parse("main"),
            StackFilePath.Parse("compose.yaml"), isSystem: false, Now);
        if (token is not null)
        {
            stack.SetDeployHook(SecretToken.Hash(token));
        }

        stacks.Add(stack);
        return stack;
    }
}
