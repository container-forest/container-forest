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

    /// <summary>Throws when the operation is never allowed for this stack.</summary>
    public void EnsureAllows(StackOperation operation)
    {
        if (IsSystem && operation is StackOperation.Down or StackOperation.Remove)
        {
            throw new DomainConflictException($"System stack '{Name}' cannot be taken down or removed.");
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
    public void RecordDeployment(Deployment deployment, bool withSecrets)
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
