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

    [Fact]
    public async Task Observers_hear_the_saved_outcome_of_a_successful_run()
    {
        var context = Context();
        var observer = new RecordingObserver(unitOfWork);

        await Pipeline([observer], new PinCommitStep()).RunAsync(context, Ct);

        var (outcome, savesBefore) = Assert.Single(observer.Outcomes);
        Assert.Same(context.Deployment, outcome.Deployment);
        Assert.Same(context.Stack, outcome.Stack);
        Assert.Same(context.Environment, outcome.Environment);
        Assert.Equal(DeploymentStatus.Succeeded, outcome.Deployment.Status);
        Assert.Equal(3, savesBefore);
    }

    [Fact]
    public async Task Observers_hear_a_failed_run_too()
    {
        var context = Context();
        var observer = new RecordingObserver(unitOfWork);

        await Pipeline([observer], new RecordingStep("a", journal, new DeploymentFailedException("no compose file"))).RunAsync(context, Ct);

        var (outcome, _) = Assert.Single(observer.Outcomes);
        Assert.Equal(DeploymentStatus.Failed, outcome.Deployment.Status);
        Assert.Equal("no compose file", outcome.Deployment.Error);
    }

    [Fact]
    public async Task A_throwing_observer_changes_nothing_for_the_run_or_the_next_observer()
    {
        var context = Context();
        var next = new RecordingObserver(unitOfWork);

        await Pipeline([new ThrowingObserver(), next], new PinCommitStep()).RunAsync(context, Ct);

        Assert.Equal(DeploymentStatus.Succeeded, context.Deployment.Status);
        Assert.Single(next.Outcomes);
    }

    private DeployPipeline Pipeline(params IDeployStep[] steps) => Pipeline([], steps);

    private DeployPipeline Pipeline(IDeploymentObserver[] observers, params IDeployStep[] steps) =>
        new(steps, observers, unitOfWork, NullLogger<DeployPipeline>.Instance);

    /// <summary>Records each outcome with how many saves happened before it arrived.</summary>
    private sealed class RecordingObserver(FakeUnitOfWork unitOfWork) : IDeploymentObserver
    {
        public List<(DeploymentOutcome Outcome, int SavesBefore)> Outcomes { get; } = [];

        public Task DeploymentFinishedAsync(DeploymentOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add((outcome, unitOfWork.Saves));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IDeploymentObserver
    {
        public Task DeploymentFinishedAsync(DeploymentOutcome outcome, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("observer bug");
    }

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
