using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.Extensions.Logging.Abstractions;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Deployments;

public class SecretRehydrationServiceTests
{
    private static readonly CommitSha Other = CommitSha.Parse(new string('b', 40));

    private readonly InMemoryStacks stacks = new();
    private readonly InMemoryDeployments deployments = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly FakeDeploymentQueue queue = new();
    private readonly EnvironmentId environmentId = EnvironmentId.New();
    private readonly SecretRehydrationService service;

    public SecretRehydrationServiceTests() =>
        service = new SecretRehydrationService(
            stacks, deployments, unitOfWork, queue, new FixedTime(Now), NullLogger<SecretRehydrationService>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Queues_the_running_commit_of_each_stack_deployed_with_secrets()
    {
        var web = Deployed("web", Commit, withSecrets: true);
        var api = Deployed("api", Other, withSecrets: true);
        Deployed("plain", Commit, withSecrets: false);
        stacks.Add(NewStack("never-deployed"));

        var queued = await service.RehydrateAsync(environmentId, Ct);

        Assert.Equal(
            [(web.Id, Commit), (api.Id, Other)],
            queued.Select(d => (d.StackId, d.Commit!)).OrderBy(p => p.Item2.Value).ToList());
        Assert.All(queued, d => Assert.Equal((DeploymentTrigger.Rehydrate, true), (d.Trigger, d.IsPinned)));
        Assert.Equal(queued.Select(d => d.Id), queue.Queued);
        Assert.Equal(1, unitOfWork.Saves);
    }

    [Fact]
    public async Task Skips_stacks_of_other_environments_and_system_stacks()
    {
        Deployed("system", Commit, withSecrets: true, isSystem: true);
        var foreign = Stack.Create(
            EnvironmentId.New(), StackName.Parse("foreign"), RepositoryId.New(), BranchName.Parse("main"),
            StackFilePath.Parse("compose.yaml"), isSystem: false, Now);
        Record(foreign, Commit, withSecrets: true);
        stacks.Add(foreign);

        Assert.Empty(await service.RehydrateAsync(environmentId, Ct));
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task Stack_with_a_deployment_in_progress_is_left_to_it()
    {
        var web = Deployed("web", Commit, withSecrets: true);
        deployments.Add(Deployment.QueueManual(web.Id, UserId.New(), ImagePullPolicy.Missing, Now));

        Assert.Empty(await service.RehydrateAsync(environmentId, Ct));
        Assert.Empty(queue.Queued);
    }

    private Stack NewStack(string name, bool isSystem = false) => Stack.Create(
        environmentId, StackName.Parse(name), RepositoryId.New(), BranchName.Parse("main"), StackFilePath.Parse("compose.yaml"), isSystem, Now);

    private Stack Deployed(string name, CommitSha commit, bool withSecrets, bool isSystem = false)
    {
        var stack = NewStack(name, isSystem);
        Record(stack, commit, withSecrets);
        stacks.Add(stack);
        return stack;
    }

    private static void Record(Stack stack, CommitSha commit, bool withSecrets)
    {
        var deployment = Deployment.QueueManual(stack.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        deployment.Start(Now);
        deployment.ResolveCommit(commit);
        deployment.Succeed(Now);
        stack.RecordDeployment(deployment, withSecrets);
    }
}
