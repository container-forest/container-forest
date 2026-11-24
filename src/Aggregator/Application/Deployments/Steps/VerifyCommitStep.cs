using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Deployments.Steps;

/// <summary>
/// For a stack that requires signed commits, lets the deployment go on only with content someone who may deploy the
/// stack endorsed (ADR-0011): a commit signed with their signing key, within the key's environments, the unsigned commit
/// they approved by its SHA, or an update commit of the aggregator on such content (ADR-0015). Runs before
/// <see cref="SopsDecryptStep"/>, so content nobody endorsed never gets the decrypted secrets. Automatic deployments
/// also stay on the line of the deployed commit.
/// </summary>
public sealed class VerifyCommitStep(CommitEndorsementService endorsements) : IDeployStep
{
    public string Name => "Verify commit signature";

    public async Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
    {
        var stack = context.Stack;
        var deployment = context.Deployment;
        if (!stack.RequireSignedCommits)
        {
            context.Info("The stack does not require signed commits.");
            return;
        }

        var commit = deployment.Commit ?? throw new InvalidOperationException("The commit is resolved by the fetch step.");
        if (deployment.Trigger == DeploymentTrigger.Rehydrate && commit == stack.DeployedCommit)
        {
            // Restoring secrets after a reboot deploys what already runs; refusing it would leave the stack down.
            deployment.RecordAlreadyDeployed();
            context.Info($"Commit {commit.Short} is already deployed; its signature is not checked again.");
            return;
        }

        var operation = deployment.PullPolicy == ImagePullPolicy.Always ? StackOperation.RedeployWithPull : StackOperation.Redeploy;
        var result = await endorsements.EvaluateAsync(context.Repository, context.Environment, stack, commit, operation, cancellationToken);
        var (problem, detail) = (result.Problem, result.Detail);
        if (problem is null && !await endorsements.DescendsFromDeployedAsync(context.Repository, stack, commit, cancellationToken))
        {
            if (deployment.IsAutomatic)
            {
                // A force-push back to an old signed commit must not roll the stack back without anyone asking.
                problem = EndorsementProblem.NotDescendant;
                detail = $"commit {commit.Short} does not descend from the deployed commit {stack.DeployedCommit!.Short} " +
                    "(the branch was rewritten, or the stack's source changed)";
            }
            else if (deployment.Trigger == DeploymentTrigger.Rollback)
            {
                context.Info($"Rolling back from {stack.DeployedCommit!.Short} to the earlier commit {commit.Short}.");
            }
            else
            {
                context.Warn(
                    $"Commit {commit.Short} does not descend from the deployed commit {stack.DeployedCommit!.Short}; " +
                    "deploying it anyway, since it was started by hand.");
            }
        }

        if (problem is null)
        {
            Record(context, commit, result);
            return;
        }

        if (deployment.ApprovedCommit == commit)
        {
            deployment.RecordApprovedUnsigned(problem.Value);
            context.Warn($"Commit {commit.Short} is not endorsed: {detail}. Deploying it as approved by hand.");
            return;
        }

        if (deployment.ApprovedCommit is { } approved && approved != commit)
        {
            problem = EndorsementProblem.ApprovalOutdated;
            detail = $"commit {approved.Short} was approved, but the branch is now at {commit.Short} ({detail})";
        }

        deployment.RecordRejected(problem.Value);
        var hint = deployment.Trigger switch
        {
            DeploymentTrigger.Manual =>
                $" To deploy it unsigned, approve commit {commit.Value} in the panel (or 'forest stack deploy --approve {commit.Value}'); " +
                "otherwise push a commit signed by someone who may deploy the stack.",
            DeploymentTrigger.Rollback =>
                $" To roll back to it unsigned, approve it in the panel (or 'forest stack rollback --to {commit.Value} --approve').",
            _ => " Push a commit signed by someone who may deploy the stack, or deploy it by hand.",
        };
        throw new DeploymentFailedException($"The stack requires signed commits: {detail}.{hint}");
    }

    private static void Record(DeployContext context, CommitSha commit, CommitEndorsementResult result)
    {
        var signer = result.Signer;
        if (result.Update is { } update)
        {
            context.Deployment.RecordImageUpdate(update.Id, signer is null ? null : (signer.UserId, signer.UserName, signer.Fingerprint));
        }
        else
        {
            context.Deployment.RecordSigned(signer!.UserId, signer.UserName, signer.Fingerprint);
        }

        signer?.Key.RecordUse(context.Now);
        context.Info(result.Update is null ? $"Commit {commit.Short} is {result.Detail}." : $"Endorsed: {result.Detail}.");
    }
}
