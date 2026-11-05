using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;

namespace ContainerForest.Aggregator.Domain.Stacks;

/// <summary>A set of services described by one compose file from a git repository, deployed to one environment.</summary>
public sealed class Stack : Entity<StackId>
{
    private Stack(
        StackId id,
        EnvironmentId environmentId,
        StackName name,
        RepositoryId repositoryId,
        BranchName branch,
        StackFilePath composePath,
        bool isSystem,
        DateTimeOffset createdAt)
        : base(id)
    {
        EnvironmentId = environmentId;
        Name = name;
        RepositoryId = repositoryId;
        Branch = branch;
        ComposePath = composePath;
        IsSystem = isSystem;
        CreatedAt = createdAt;
    }

    // For EF Core.
    private Stack()
    {
        Name = null!;
        Branch = null!;
        ComposePath = null!;
    }

    public EnvironmentId EnvironmentId { get; private init; }

    /// <summary>Compose project name and directory name on the host; never changes, see <see cref="StackName"/>.</summary>
    public StackName Name { get; private init; }

    public RepositoryId RepositoryId { get; private set; }

    public BranchName Branch { get; private set; }

    /// <summary>Path to the compose file inside the repository.</summary>
    public StackFilePath ComposePath { get; private set; }

    /// <summary>A stack of the aggregator or the agent itself: it cannot be taken down and is updated through a helper container.</summary>
    public bool IsSystem { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public CommitSha? DeployedCommit { get; private set; }

    public DateTimeOffset? DeployedAt { get; private set; }

    /// <summary>
    /// The deployed commit had SOPS-decrypted files, which the agent keeps on tmpfs: a reboot of the host loses them,
    /// and the stack is deployed again when its agent reports one.
    /// </summary>
    public bool DeployedWithSecrets { get; private set; }

    /// <summary>
    /// A push to <see cref="Branch"/> deploys the stack, when its repository has a webhook. Off by default: whoever
    /// set the stack up decides whether the branch is ready to go live on every push.
    /// </summary>
    public bool DeployOnPush { get; private set; }

    /// <summary>
    /// After a deployment, the agent restarts the stack when a file other than the compose file changed. Containers
    /// read bind-mounted configuration when they start, and compose recreates them only for a changed definition.
    /// Off by default: a restart interrupts the services, and many stacks have no such files.
    /// </summary>
    public bool RestartOnFileChange { get; private set; }

    /// <summary>
    /// SHA-256 of the deploy hook token (<see cref="SecretToken.Hash"/>), never the token: CI presents the token to
    /// deploy the branch head with fresh images. Null when the stack has no deploy hook.
    /// </summary>
    public string? DeployHookTokenHash { get; private set; }

    public bool HasDeployHook => DeployHookTokenHash is not null;

    /// <summary>
    /// Deploy only content someone who may deploy the stack endorsed: a commit signed with their signing key, or one
    /// they approved by its SHA (ADR-0011). Off by default; then the branch head is deployed as it is.
    /// </summary>
    public bool RequireSignedCommits { get; private set; }

    /// <summary>
    /// Every image must be pinned by a digest: a signature covers "image: app:1.4", not what the registry serves
    /// under that tag. Off by default.
    /// </summary>
    public bool RequireImageDigests { get; private set; }

    private List<string> policyAllowances = [];

    /// <summary>
    /// Exceptions to the environment's compose policy for this stack only, as <see cref="Policies.PolicyAllowance.Value"/>
    /// (ADR-0012). Administrators of the system or of the environment set them.
    /// </summary>
    public IReadOnlyList<string> PolicyAllowances => policyAllowances;

    /// <summary>
    /// The compose policy's facts about the deployed version, serialized; null until a deployment with the policy
    /// on succeeds. Checked again when the policy changes, since running containers do not stop when it tightens.
    /// </summary>
    public string? DeployedComposeFacts { get; private set; }

    public static Stack Create(
        EnvironmentId environmentId,
        StackName name,
        RepositoryId repositoryId,
        BranchName branch,
        StackFilePath composePath,
        bool isSystem,
        DateTimeOffset now) =>
        new(StackId.New(), environmentId, name, repositoryId, branch, composePath, isSystem, now);

    public void ChangeSource(RepositoryId repositoryId, BranchName branch, StackFilePath composePath)
    {
        RepositoryId = repositoryId;
        Branch = branch;
        ComposePath = composePath;
    }

    /// <exception cref="DomainConflictException">Turning it on for a system stack, which the pipeline does not deploy.</exception>
    public void SetDeployOnPush(bool enabled)
    {
        if (enabled && RequiresUpdateHelper(StackOperation.Redeploy))
        {
            throw new DomainConflictException(
                $"System stack '{Name}' can only be updated through the helper container, so a push cannot deploy it.");
        }

        DeployOnPush = enabled;
    }

    /// <exception cref="DomainConflictException">Turning it on for a system stack, which the pipeline does not deploy.</exception>
    public void SetRestartOnFileChange(bool enabled)
    {
        if (enabled && RequiresUpdateHelper(StackOperation.Restart))
        {
            throw new DomainConflictException(
                $"System stack '{Name}' can only be updated through the helper container, so a deployment cannot restart it.");
        }

        RestartOnFileChange = enabled;
    }

    /// <exception cref="DomainConflictException">Turning it on for a system stack, which the pipeline does not deploy.</exception>
    public void SetRequireSignedCommits(bool enabled)
    {
        EnsureDeployable(enabled, "require signed commits");
        RequireSignedCommits = enabled;
    }

    /// <exception cref="DomainConflictException">Turning it on for a system stack, which the pipeline does not deploy.</exception>
    public void SetRequireImageDigests(bool enabled)
    {
        EnsureDeployable(enabled, "require image digests");
        RequireImageDigests = enabled;
    }

    /// <exception cref="Common.DomainValidationException">An allowance is not valid.</exception>
    public void SetPolicyAllowances(IEnumerable<string> allowances) =>
        policyAllowances = Policies.ComposePolicy.NormalizeAllowances(allowances);

    private void EnsureDeployable(bool enabled, string what)
    {
        if (enabled && RequiresUpdateHelper(StackOperation.Redeploy))
        {
            throw new DomainConflictException(
                $"System stack '{Name}' can only be updated through the helper container, so it cannot {what}.");
        }
    }

    /// <summary>Sets the deploy hook to a new token, given as its hash; the previous token stops working.</summary>
    /// <exception cref="DomainConflictException">A system stack, which the pipeline does not deploy.</exception>
    public void SetDeployHook(string tokenHash)
    {
        if (RequiresUpdateHelper(StackOperation.RedeployWithPull))
        {
            throw new DomainConflictException(
                $"System stack '{Name}' can only be updated through the helper container, so a deploy hook cannot deploy it.");
        }

        if (tokenHash.Length != 64 || !tokenHash.All(char.IsAsciiHexDigitLower))
        {
            throw new DomainValidationException("A deploy hook is stored as the token's SHA-256 in lowercase hex.");
        }

        DeployHookTokenHash = tokenHash;
    }

    public void RemoveDeployHook() => DeployHookTokenHash = null;

    /// <summary>True when <paramref name="token"/> is the deploy hook's token; compared in constant time.</summary>
    public bool AcceptsDeployHook(string token) =>
        DeployHookTokenHash is { } hash && SecretToken.Matches(token, hash);

    /// <summary>Throws when the operation is never allowed for this stack.</summary>
    public void EnsureAllows(StackOperation operation)
    {
        if (IsSystem && operation is StackOperation.Down or StackOperation.Remove)
        {
            throw new DomainConflictException($"System stack '{Name}' cannot be taken down or removed.");
        }
    }

    /// <summary>
    /// Throws unless the stack may be removed from the system. A deployed stack must be taken down first: its
    /// containers would keep running with nothing in the system that manages them.
    /// </summary>
    /// <exception cref="DomainConflictException">A system stack, or one with a deployed commit.</exception>
    public void EnsureRemovable()
    {
        EnsureAllows(StackOperation.Remove);
        if (DeployedCommit is not null)
        {
            throw new DomainConflictException($"Stack '{Name}' is deployed; take it down before removing it.");
        }
    }

    /// <summary>
    /// True when the operation must run through the one-off helper container: an agent running
    /// compose up on its own stack would stop itself halfway through the command.
    /// </summary>
    public bool RequiresUpdateHelper(StackOperation operation) =>
        IsSystem && operation is StackOperation.Restart or StackOperation.Redeploy or StackOperation.RedeployWithPull;

    /// <summary>Remembers the commit of a successful deployment of this stack.</summary>
    /// <param name="withSecrets">The deployment sent secret files to the agent.</param>
    /// <param name="composeFacts">The compose policy's facts about the deployed version; null keeps the previous ones.</param>
    public void RecordDeployment(Deployment deployment, bool withSecrets, string? composeFacts = null)
    {
        if (deployment.StackId != Id)
        {
            throw new DomainConflictException($"Deployment {deployment.Id} belongs to another stack.");
        }

        if (deployment.Status != DeploymentStatus.Succeeded || deployment.Commit is null || deployment.FinishedAt is null)
        {
            throw new DomainConflictException($"Deployment {deployment.Id} has not succeeded.");
        }

        // A slower, older deployment must not overwrite the result of a newer one.
        if (DeployedAt is { } deployedAt && deployment.FinishedAt < deployedAt)
        {
            return;
        }

        DeployedCommit = deployment.Commit;
        DeployedAt = deployment.FinishedAt;
        DeployedWithSecrets = withSecrets;
        DeployedComposeFacts = composeFacts ?? DeployedComposeFacts;
    }

    /// <summary>
    /// True when a reboot of the host took files this stack needs: it is deployed with secrets and is not a system
    /// stack, which the pipeline does not deploy (<see cref="RequiresUpdateHelper"/>).
    /// </summary>
    public bool NeedsRehydration => DeployedCommit is not null && DeployedWithSecrets && !IsSystem;

    /// <summary>
    /// Forgets the deployed commit after a successful docker compose down: the stack has no containers any more,
    /// so nothing of that commit is running. The files stay on the host; the next deployment records its own commit.
    /// </summary>
    public void RecordTakenDown()
    {
        EnsureAllows(StackOperation.Down);
        DeployedCommit = null;
        DeployedAt = null;
        DeployedWithSecrets = false;
    }
}
