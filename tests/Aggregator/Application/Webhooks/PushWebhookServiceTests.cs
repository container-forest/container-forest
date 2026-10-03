using System.Security.Cryptography;
using System.Text;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Webhooks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Aggregator.Application.Tests.Deployments;
using Microsoft.Extensions.Logging.Abstractions;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Webhooks;

public class PushWebhookServiceTests
{
    private const string Secret = "webhook-secret";
    private static readonly CommitSha Pushed = CommitSha.Parse(new string('b', 40));

    private readonly InMemoryGitRepositories repositories = new();
    private readonly InMemoryStacks stacks = new();
    private readonly InMemoryDeployments deployments = new();
    private readonly FakeAgentChannel agents = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly FakeDeploymentQueue queue = new();
    private readonly FakeSecretProtector protector = new();
    private readonly PushDeploymentGate gate = new();
    private readonly GitRepository repository = GitRepository.Create("infra", "https://git.example.com/infra.git", Now);
    private readonly EnvironmentId environmentId = EnvironmentId.New();
    private readonly PushWebhookService service;

    public PushWebhookServiceTests()
    {
        repository.SetWebhookSecret(protector.Protect(SecretPurpose.WebhookSecret, Secret));
        repositories.Add(repository);
        service = new PushWebhookService(
            [new GiteaWebhook(), new GitHubWebhook(), new GitLabWebhook()],
            repositories,
            stacks,
            deployments,
            agents,
            protector,
            unitOfWork,
            queue,
            gate,
            new FixedTime(Now),
            NullLogger<PushWebhookService>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Push_queues_a_webhook_deployment_of_each_stack_that_deploys_the_branch_on_push()
    {
        var web = AddStack("web");
        var api = AddStack("api");
        AddStack("manual", deployOnPush: false);
        AddStack("other-branch", branch: "dev");
        AddStack("other-repository", repositoryId: RepositoryId.New());

        var result = await service.ReceiveAsync(repository.Id, Push("main"), Ct);

        Assert.Equal(("GitHub", WebhookOutcome.Push), (result.Provider, result.Outcome));
        Assert.Equal(
            [("api", PushStackOutcome.Queued), ("web", PushStackOutcome.Queued)],
            result.Stacks.Select(s => (s.Name.Value, s.Outcome)).Order().ToList());
        Assert.All(deployments.All, d => Assert.Equal((DeploymentTrigger.Webhook, null, false), (d.Trigger, d.RequestedBy, d.IsPinned)));
        Assert.Equivalent(new[] { api.Id, web.Id }, deployments.All.Select(d => d.StackId));
        Assert.Equal(deployments.All.Select(d => d.Id), queue.Queued);
    }

    [Fact]
    public async Task Wrong_signature_unknown_repository_and_repository_without_webhook_are_rejected_alike()
    {
        AddStack("web");
        var withoutWebhook = GitRepository.Create("plain", "https://git.example.com/plain.git", Now);
        repositories.Add(withoutWebhook);

        await Assert.ThrowsAsync<WebhookRejectedException>(() => service.ReceiveAsync(repository.Id, Push("main", secret: "guess"), Ct));
        await Assert.ThrowsAsync<WebhookRejectedException>(() => service.ReceiveAsync(RepositoryId.New(), Push("main"), Ct));
        await Assert.ThrowsAsync<WebhookRejectedException>(() => service.ReceiveAsync(withoutWebhook.Id, Push("main"), Ct));
        Assert.Empty(deployments.All);
    }

    [Fact]
    public async Task Delivery_from_an_unknown_git_server_is_rejected()
    {
        var delivery = new WebhookDelivery(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Encoding.UTF8.GetBytes("{}"));

        await Assert.ThrowsAsync<WebhookRejectedException>(() => service.ReceiveAsync(repository.Id, delivery, Ct));
    }

    [Fact]
    public async Task Unreadable_secret_is_rejected()
    {
        repository.SetWebhookSecret("lost key ring");

        await Assert.ThrowsAsync<WebhookRejectedException>(() => service.ReceiveAsync(repository.Id, Push("main"), Ct));
    }

    [Fact]
    public async Task Ping_tags_deleted_branches_and_other_events_deploy_nothing()
    {
        AddStack("web");

        Assert.Equal(WebhookOutcome.Pong, (await service.ReceiveAsync(repository.Id, Signed("{}", "ping"), Ct)).Outcome);
        Assert.Equal(WebhookOutcome.Ignored, (await service.ReceiveAsync(repository.Id, Signed("{}", "issues"), Ct)).Outcome);
        Assert.Equal(WebhookOutcome.Ignored, (await service.ReceiveAsync(repository.Id, Push("main", gitRef: "refs/tags/v1"), Ct)).Outcome);
        Assert.Equal(WebhookOutcome.Ignored, (await service.ReceiveAsync(repository.Id, Push("main", after: new string('0', 40)), Ct)).Outcome);
        Assert.Empty(deployments.All);
    }

    [Fact]
    public async Task Offline_agent_gets_nothing_queued()
    {
        AddStack("web");
        agents.Online = false;

        var result = await service.ReceiveAsync(repository.Id, Push("main"), Ct);

        Assert.Equal(PushStackOutcome.AgentOffline, Assert.Single(result.Stacks).Outcome);
        Assert.Empty(deployments.All);
    }

    [Fact]
    public async Task Stack_already_running_the_pushed_commit_is_left_alone()
    {
        var web = AddStack("web");
        var previous = Deployment.QueueManual(web.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        previous.Start(Now);
        previous.ResolveCommit(Pushed);
        previous.Succeed(Now);
        web.RecordDeployment(previous, withSecrets: false);

        var result = await service.ReceiveAsync(repository.Id, Push("main"), Ct);

        Assert.Equal(PushStackOutcome.AlreadyDeployed, Assert.Single(result.Stacks).Outcome);
        Assert.Empty(queue.Queued);
    }

    [Fact]
    public async Task Queued_deployment_takes_the_push_with_it()
    {
        var web = AddStack("web");
        deployments.Add(Deployment.QueueManual(web.Id, UserId.New(), ImagePullPolicy.Missing, Now));

        var result = await service.ReceiveAsync(repository.Id, Push("main"), Ct);

        Assert.Equal(PushStackOutcome.AlreadyQueued, Assert.Single(result.Stacks).Outcome);
        Assert.Single(deployments.All);
    }

    [Fact]
    public async Task Push_during_a_deployment_of_an_older_commit_is_deployed_after_it()
    {
        var web = AddStack("web");
        var running = Deployment.QueueManual(web.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        running.Start(Now);
        running.ResolveCommit(Commit);
        deployments.Add(running);

        var result = await service.ReceiveAsync(repository.Id, Push("main"), Ct);
        Assert.Equal(PushStackOutcome.AfterRunning, Assert.Single(result.Stacks).Outcome);
        Assert.Single(deployments.All);

        running.Succeed(Now);
        await FollowUp().DeploymentFinishedAsync(Outcome(running, web), Ct);

        var followUp = Assert.Single(deployments.All, d => d.Id != running.Id);
        Assert.Equal(DeploymentTrigger.Webhook, followUp.Trigger);
        Assert.Equal([followUp.Id], queue.Queued);

        // Remembered once: the next deployment to finish queues nothing.
        followUp.Start(Now);
        followUp.Fail("boom", Now);
        await FollowUp().DeploymentFinishedAsync(Outcome(followUp, web), Ct);
        Assert.Equal(2, deployments.All.Count);
    }

    [Fact]
    public async Task Push_of_the_commit_being_deployed_is_not_deployed_again()
    {
        var web = AddStack("web");
        var running = Deployment.QueueManual(web.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        running.Start(Now);
        running.ResolveCommit(Pushed);
        deployments.Add(running);

        var result = await service.ReceiveAsync(repository.Id, Push("main"), Ct);
        running.Succeed(Now);
        await FollowUp().DeploymentFinishedAsync(Outcome(running, web), Ct);

        Assert.Equal(PushStackOutcome.AlreadyRunning, Assert.Single(result.Stacks).Outcome);
        Assert.Single(deployments.All);
    }

    [Fact]
    public async Task Follow_up_is_dropped_when_the_agent_went_offline()
    {
        var web = AddStack("web");
        var running = Deployment.QueueManual(web.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        running.Start(Now);
        deployments.Add(running);
        await service.ReceiveAsync(repository.Id, Push("main"), Ct);

        agents.Online = false;
        running.Fail("agent disconnected", Now);
        await FollowUp().DeploymentFinishedAsync(Outcome(running, web), Ct);

        Assert.Single(deployments.All);
    }

    [Fact]
    public async Task Gitea_and_GitLab_pushes_are_accepted()
    {
        AddStack("web");
        var body = Encoding.UTF8.GetBytes($$"""{"ref":"refs/heads/main","after":"{{Pushed}}"}""");

        var gitea = await service.ReceiveAsync(
            repository.Id,
            Delivery(body, ("X-Gitea-Event", "push"), ("X-GitHub-Event", "push"), ("X-Gitea-Signature", Hmac(body, Secret))),
            Ct);
        deployments.All.Single().Start(Now);
        deployments.All.Single().Fail("boom", Now);
        var gitlab = await service.ReceiveAsync(
            repository.Id, Delivery(body, ("X-Gitlab-Event", "Push Hook"), ("X-Gitlab-Token", Secret)), Ct);

        Assert.Equal(("Gitea", PushStackOutcome.Queued), (gitea.Provider, gitea.Stacks.Single().Outcome));
        Assert.Equal(("GitLab", PushStackOutcome.Queued), (gitlab.Provider, gitlab.Stacks.Single().Outcome));
    }

    private PushFollowUpObserver FollowUp() =>
        new(gate, deployments, agents, unitOfWork, queue, new FixedTime(Now), NullLogger<PushFollowUpObserver>.Instance);

    private DeploymentOutcome Outcome(Deployment deployment, Stack stack) =>
        new(deployment, stack, DockerEnvironment.Create("prod", isPrivate: false, Now));

    private Stack AddStack(string name, bool deployOnPush = true, string branch = "main", RepositoryId? repositoryId = null)
    {
        var stack = Stack.Create(
            environmentId, StackName.Parse(name), repositoryId ?? repository.Id, BranchName.Parse(branch),
            StackFilePath.Parse("compose.yaml"), isSystem: false, Now);
        stack.SetDeployOnPush(deployOnPush);
        stacks.Add(stack);
        return stack;
    }

    private static WebhookDelivery Push(string branch, string secret = Secret, string? gitRef = null, string? after = null) =>
        Signed($$"""{"ref":"{{gitRef ?? $"refs/heads/{branch}"}}","after":"{{after ?? Pushed.Value}}"}""", "push", secret);

    private static WebhookDelivery Signed(string json, string gitHubEvent, string secret = Secret)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return Delivery(body, ("X-GitHub-Event", gitHubEvent), ("X-Hub-Signature-256", "sha256=" + Hmac(body, secret)));
    }

    private static string Hmac(byte[] body, string secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    private static WebhookDelivery Delivery(byte[] body, params (string Name, string Value)[] headers) =>
        new(headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase), body);
}
