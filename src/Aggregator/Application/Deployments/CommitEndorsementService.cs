using System.Text;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Images;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Images;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Deployments;

/// <summary>The user whose signing key signed a commit, as a deployment records it.</summary>
public sealed record CommitSigner(UserId UserId, string UserName, string Fingerprint, SigningKey Key, string Description);

/// <summary>What endorses a commit, or why nothing does.</summary>
/// <param name="Problem">Null when the commit is endorsed.</param>
/// <param name="Detail">Why not, for the deployment log; what endorses it otherwise.</param>
/// <param name="Signer">Who signed the commit or, for an update commit, the content under the updates.</param>
/// <param name="Update">The record of an update commit; null for any other.</param>
/// <param name="ApprovedRoot">The content under the updates was approved by its SHA rather than signed.</param>
public sealed record CommitEndorsementResult(
    EndorsementProblem? Problem,
    string Detail,
    CommitSigner? Signer = null,
    ImageUpdateRecord? Update = null,
    bool ApprovedRoot = false)
{
    public bool IsEndorsed => Problem is null;
}

/// <summary>
/// Decides whether someone who may deploy the stack endorsed a commit (ADR-0011): a signature with their key within its
/// environments or, for an update commit of the aggregator (ADR-0015), a matching record on top of endorsed content.
/// Used by <see cref="Steps.VerifyCommitStep"/> before decrypting secrets, and by image updates before committing on a head.
/// </summary>
public sealed class CommitEndorsementService(
    ICommitInspector inspector,
    ICommitSignatureVerifier verifier,
    ISigningKeyRepository keys,
    IUserRepository users,
    IRoleAssignmentRepository roleAssignments,
    IImageUpdateRecordRepository updates,
    IDeploymentRepository deployments,
    IComposeImageRewriter rewriter)
{
    /// <summary>Update commits in a row before the content under them must be endorsed by a person again.</summary>
    public const int MaxUpdateChain = 100;

    /// <param name="operation">What the deployment does; the signer must be allowed to.</param>
    public async Task<CommitEndorsementResult> EvaluateAsync(
        GitRepository repository,
        DockerEnvironment environment,
        Stack stack,
        CommitSha commit,
        StackOperation operation,
        CancellationToken cancellationToken)
    {
        var signed = await EvaluateSignatureAsync(repository, environment, stack, commit, operation, cancellationToken);
        if (signed.IsEndorsed || signed.Problem is not (EndorsementProblem.Unsigned or EndorsementProblem.UnknownKey))
        {
            return signed;
        }

        // The aggregator's own key is unknown on purpose: an update commit is endorsed by its record, not its signature.
        var record = await updates.FindByCommitAsync(stack.Id, commit, cancellationToken);
        return record is null
            ? signed
            : await EvaluateUpdateChainAsync(repository, environment, stack, record, operation, cancellationToken);
    }

    private async Task<CommitEndorsementResult> EvaluateUpdateChainAsync(
        GitRepository repository,
        DockerEnvironment environment,
        Stack stack,
        ImageUpdateRecord first,
        StackOperation operation,
        CancellationToken cancellationToken)
    {
        var record = first;
        for (var step = 0; step < MaxUpdateChain; step++)
        {
            if (await CheckDiffAsync(repository, record, cancellationToken) is { } mismatch)
            {
                return new CommitEndorsementResult(EndorsementProblem.ImageUpdateMismatch, mismatch);
            }

            var parent = record.Parent;
            var signed = await EvaluateSignatureAsync(repository, environment, stack, parent, operation, cancellationToken);
            if (signed.IsEndorsed)
            {
                return new CommitEndorsementResult(
                    null, $"commit {first.Commit.Short} is an image update on {parent.Short}, {signed.Detail}", signed.Signer, first);
            }

            if (await deployments.WasApprovedAsync(stack.Id, parent, cancellationToken))
            {
                return new CommitEndorsementResult(
                    null, $"commit {first.Commit.Short} is an image update on {parent.Short}, which was approved by its SHA",
                    Update: first, ApprovedRoot: true);
            }

            if (signed.Problem is not (EndorsementProblem.Unsigned or EndorsementProblem.UnknownKey)
                || await updates.FindByCommitAsync(stack.Id, parent, cancellationToken) is not { } next)
            {
                return new CommitEndorsementResult(
                    EndorsementProblem.ImageUpdateMismatch,
                    $"commit {first.Commit.Short} is an image update, but the content under it is not endorsed: {signed.Detail}");
            }

            record = next;
        }

        return new CommitEndorsementResult(
            EndorsementProblem.ImageUpdateMismatch,
            $"commit {first.Commit.Short} is the last of more than {MaxUpdateChain} image updates in a row; " +
            "push a commit signed by someone who may deploy the stack");
    }

    /// <summary>Null when the commit changes exactly what its record says; otherwise what differs.</summary>
    private async Task<string?> CheckDiffAsync(GitRepository repository, ImageUpdateRecord record, CancellationToken cancellationToken)
    {
        var path = record.ComposePath.Value;
        var diff = await inspector.DiffAsync(repository, record.Commit, path, cancellationToken);
        if (diff is null || diff.Parent != record.Parent)
        {
            return $"commit {record.Commit.Short} is not the recorded image update on {record.Parent.Short}";
        }

        if (diff.ChangedPaths.Count != 1 || diff.ChangedPaths[0] != path || diff.ModeChanged || diff.Before is null || diff.After is null)
        {
            return $"commit {record.Commit.Short} changes more than the image values in {path}";
        }

        // The difference is computed again rather than trusted: the commit must be the parent with exactly these changes.
        var expected = rewriter.Apply(Decode(diff.Before), record.Changes);
        return expected is not null && expected == Decode(diff.After)
            ? null
            : $"commit {record.Commit.Short} changes {path} beyond the recorded image values";
    }

    private static string Decode(byte[] content) => new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(content);

    /// <summary>True when the commit is the deployed one or descends from it; always for a stack never deployed.</summary>
    public async Task<bool> DescendsFromDeployedAsync(GitRepository repository, Stack stack, CommitSha commit, CancellationToken cancellationToken) =>
        stack.DeployedCommit is not { } deployed
        || deployed == commit
        || await inspector.DescendsFromAsync(repository, commit, deployed, cancellationToken);

    /// <summary>The signature check of ADR-0011; also for the parents of update commits.</summary>
    public async Task<CommitEndorsementResult> EvaluateSignatureAsync(
        GitRepository repository,
        DockerEnvironment environment,
        Stack stack,
        CommitSha commit,
        StackOperation operation,
        CancellationToken cancellationToken)
    {
        var data = await inspector.ReadSignatureAsync(repository, commit, cancellationToken);
        if (data is null)
        {
            return new(EndorsementProblem.Unsigned, $"commit {commit.Short} is not signed");
        }

        if (!SshSignature.TryParse(data.Signature, out var signature))
        {
            return SshSignature.IsSshSignature(data.Signature)
                ? new(EndorsementProblem.UnsupportedSignature, $"commit {commit.Short} has an SSH signature with a key type that is not accepted")
                : new(EndorsementProblem.UnsupportedSignature, $"commit {commit.Short} is signed with GPG or X.509; only SSH signatures are checked");
        }

        if (signature.Namespace != SshSignature.GitNamespace)
        {
            return new(EndorsementProblem.InvalidSignature, $"the signature of commit {commit.Short} was not made for git");
        }

        var fingerprint = signature.PublicKey.Fingerprint;
        var key = await keys.FindByFingerprintAsync(fingerprint, cancellationToken);
        if (key is null)
        {
            return new(EndorsementProblem.UnknownKey, $"commit {commit.Short} is signed with key {fingerprint}, which no user has added");
        }

        // Cryptography first: a forged signature must not even get the key's owner named in the log.
        if (!await verifier.VerifyAsync(key.ParsedKey, signature, data.SignedData, cancellationToken))
        {
            return new(EndorsementProblem.InvalidSignature, $"the signature of commit {commit.Short} does not verify with key {fingerprint}");
        }

        var owner = await users.FindAsync(key.UserId, cancellationToken);
        if (owner is not { IsActive: true })
        {
            return new(EndorsementProblem.SignerMayNotDeploy, $"commit {commit.Short} is signed by a deactivated user's key '{key.Name}'");
        }

        var who = $"{owner.UserName}'s key '{key.Name}' ({fingerprint})";
        if (!key.Scope.Reaches(environment.Id))
        {
            return new(EndorsementProblem.KeyOutsideScope, $"commit {commit.Short} is signed with {who}, which is limited to other environments");
        }

        var assignments = await roleAssignments.ListForUserAsync(owner.Id, cancellationToken);
        if (!AccessPolicy.CanPerform(new Actor(owner, key.Scope), environment, stack, assignments, operation))
        {
            return new(EndorsementProblem.SignerMayNotDeploy, $"commit {commit.Short} is signed with {who}, but {owner.UserName} may not deploy the stack");
        }

        var addedBy = key.AddedByAdministrator ? ", added by an administrator" : "";
        return new(null, $"signed with {who}{addedBy}", new CommitSigner(owner.Id, owner.UserName, fingerprint, key, who + addedBy));
    }
}
