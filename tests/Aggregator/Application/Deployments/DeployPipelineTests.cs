using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Deployments;
using Microsoft.Extensions.Logging.Abstractions;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Deployments;

public class DeployPipelineTests
{
    private readonly List<string> journal = [];
    private readonly FakeUnitOfWork unitOfWork = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Runs_steps_in_order_and_records_the_deployed_commit()
    {
        var context = Context();
        var pipeline = Pipeline(new RecordingStep("a", journal), new PinCommitStep(), new RecordingStep("b", journal));

        await pipeline.RunAsync(context, Ct);

        Assert.Equal(["a", "b"], journal);
        Assert.Equal(DeploymentStatus.Succeeded, context.Deployment.Status);
        Assert.Equal(Commit, context.Stack.DeployedCommit);
        Assert.Contains(Lines(context, DeploymentLogLevel.Info), l => l == "Step: a");
    }

    [Fact]
    public async Task First_failing_step_ends_the_run_with_its_message()
    {
        var context = Context();
        var pipeline = Pipeline(
            new RecordingStep("a", journal, new DeploymentFailedException("Branch 'main' does not exist.")),
            new RecordingStep("b", journal));

        await pipeline.RunAsync(context, Ct);

        Assert.Equal(["a"], journal);
        Assert.Equal(DeploymentStatus.Failed, context.Deployment.Status);
        Assert.Equal("Branch 'main' does not exist.", context.Deployment.Error);
        Assert.Equal(["Branch 'main' does not exist."], Lines(context, DeploymentLogLevel.Error));
        Assert.Null(context.Stack.DeployedCommit);
    }

    [Fact]
    public async Task Agent_errors_are_shown_as_they_are()
    {
        var context = Context();
        var pipeline = Pipeline(new RecordingStep(
            "a", journal, new AgentCommandFailedException(context.Environment.Id, AgentErrorKind.CommandFailed, "pull access denied")));

        await pipeline.RunAsync(context, Ct);

        Assert.Equal("pull access denied", context.Deployment.Error);
    }

    [Fact]
    public async Task Unexpected_errors_do_not_leak_details()
    {
        var context = Context();
        var pipeline = Pipeline(new RecordingStep("a", journal, new InvalidOperationException("connection string: secret")));

        await pipeline.RunAsync(context, Ct);

        Assert.Equal(DeploymentStatus.Failed, context.Deployment.Status);
        Assert.DoesNotContain("secret", context.Deployment.Error);
    }

    [Fact]
    public async Task Succeeding_without_a_commit_is_a_failure()
    {
        var context = Context();

        await Pipeline(new RecordingStep("a", journal)).RunAsync(context, Ct);

        Assert.Equal(DeploymentStatus.Failed, context.Deployment.Status);
    }

    [Fact]
    public async Task Saves_after_start_after_each_step_and_at_the_end()
    {
        await Pipeline(new PinCommitStep(), new RecordingStep("a", journal)).RunAsync(Context(), Ct);

        Assert.Equal(4, unitOfWork.Saves);
    }

    private DeployPipeline Pipeline(params IDeployStep[] steps) =>
        new(steps, unitOfWork, NullLogger<DeployPipeline>.Instance);

    private sealed class PinCommitStep : IDeployStep
    {
        public string Name => "pin";

        public Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
        {
            context.Deployment.ResolveCommit(Commit);
            return Task.CompletedTask;
        }
    }
}
