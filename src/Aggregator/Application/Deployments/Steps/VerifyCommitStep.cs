using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Deployments.Steps;

/// <summary>
/// For a stack that requires signed commits, lets the deployment go on only with content someone who may deploy the
/// stack endorsed (ADR-0011): a commit signed with their signing key, within the key's environments, or the unsigned
/// commit they approved by its SHA. Runs before <see cref="SopsDecryptStep"/>, so content nobody endorsed never gets
/// the decrypted secrets. Automatic deployments also stay on the line of the deployed commit.
/// </summary>
public sealed class VerifyCommitStep(
    ICommitInspector inspector,
    ICommitSignatureVerifier verifier,
    ISigningKeyRepository keys,
    IUserRepository users,
    IRoleAssignmentRepository roleAssignments) : IDeployStep
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

        var (problem, detail) = await EvaluateAsync(context, commit, cancellationToken);
        if (problem is null && !await DescendsFromDeployedAsync(context, commit, cancellationToken))
        {
            if (deployment.IsAutomatic)
            {
                // A force-push back to an old signed commit must not roll the stack back without anyone asking.
                problem = EndorsementProblem.NotDescendant;
                detail = $"commit {commit.Short} does not descend from the deployed commit {stack.DeployedCommit!.Short} " +
                    "(the branch was rewritten, or the stack's source changed)";
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
        var hint = deployment.Trigger == DeploymentTrigger.Manual
            ? $" To deploy it unsigned, approve commit {commit.Value} in the panel (or 'forest stack deploy --approve {commit.Value}'); " +
                "otherwise push a commit signed by someone who may deploy the stack."
            : " Push a commit signed by someone who may deploy the stack, or deploy it by hand.";
        throw new DeploymentFailedException($"The stack requires signed commits: {detail}.{hint}");
    }

    private async Task<(EndorsementProblem? Problem, string Detail)> EvaluateAsync(
        DeployContext context, CommitSha commit, CancellationToken cancellationToken)
    {
        var data = await inspector.ReadSignatureAsync(context.Repository, commit, cancellationToken);
        if (data is null)
        {
            return (EndorsementProblem.Unsigned, $"commit {commit.Short} is not signed");
        }

        if (!SshSignature.TryParse(data.Signature, out var signature))
        {
            return SshSignature.IsSshSignature(data.Signature)
                ? (EndorsementProblem.UnsupportedSignature, $"commit {commit.Short} has an SSH signature with a key type that is not accepted")
                : (EndorsementProblem.UnsupportedSignature, $"commit {commit.Short} is signed with GPG or X.509; only SSH signatures are checked");
        }

        if (signature.Namespace != SshSignature.GitNamespace)
        {
            return (EndorsementProblem.InvalidSignature, $"the signature of commit {commit.Short} was not made for git");
        }

        var fingerprint = signature.PublicKey.Fingerprint;
        var key = await keys.FindByFingerprintAsync(fingerprint, cancellationToken);
        if (key is null)
        {
            return (EndorsementProblem.UnknownKey, $"commit {commit.Short} is signed with key {fingerprint}, which no user has added");
        }

        // Cryptography first: a forged signature must not even get the key's owner named in the log.
        if (!await verifier.VerifyAsync(key.ParsedKey, signature, data.SignedData, cancellationToken))
        {
            return (EndorsementProblem.InvalidSignature, $"the signature of commit {commit.Short} does not verify with key {fingerprint}");
        }

        var owner = await users.FindAsync(key.UserId, cancellationToken);
        if (owner is not { IsActive: true })
        {
            return (EndorsementProblem.SignerMayNotDeploy, $"commit {commit.Short} is signed by a deactivated user's key '{key.Name}'");
        }

        var who = $"{owner.UserName}'s key '{key.Name}' ({fingerprint})";
        if (!key.Scope.Reaches(context.Environment.Id))
        {
            return (EndorsementProblem.KeyOutsideScope, $"commit {commit.Short} is signed with {who}, which is limited to other environments");
        }

        var assignments = await roleAssignments.ListForUserAsync(owner.Id, cancellationToken);
        var operation = context.Deployment.PullPolicy == ImagePullPolicy.Always ? StackOperation.RedeployWithPull : StackOperation.Redeploy;
        if (!AccessPolicy.CanPerform(new Actor(owner, key.Scope), context.Environment, context.Stack, assignments, operation))
        {
            return (EndorsementProblem.SignerMayNotDeploy, $"commit {commit.Short} is signed with {who}, but {owner.UserName} may not deploy the stack");
        }

        context.Deployment.RecordSigned(owner.Id, owner.UserName, fingerprint);
        key.RecordUse(context.Now);
        var addedBy = key.AddedByAdministrator ? ", added by an administrator" : "";
        context.Info($"Commit {commit.Short} is signed with {who}{addedBy}.");
        return (null, "");
    }

    private async Task<bool> DescendsFromDeployedAsync(DeployContext context, CommitSha commit, CancellationToken cancellationToken) =>
        context.Stack.DeployedCommit is not { } deployed
        || deployed == commit
        || await inspector.DescendsFromAsync(context.Repository, commit, deployed, cancellationToken);
}
