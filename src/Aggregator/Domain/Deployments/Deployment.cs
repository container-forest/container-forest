using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Domain.Deployments;

public enum DeploymentStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public enum DeploymentTrigger
{
    Manual,
    Webhook,

    /// <summary>The host rebooted and lost its file secrets (tmpfs); the deployed commit is deployed again.</summary>
    Rehydrate,

    /// <summary>CI called the stack's deploy hook after pushing an image: the branch head is deployed with fresh images.</summary>
    DeployHook,
}

public enum DeploymentLogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>One line of a deployment log: pipeline messages and the output of docker compose on the agent.</summary>
/// <param name="Sequence">Position in the log, from 0; lines are ordered by it.</param>
public sealed record DeploymentLogLine(int Sequence, DateTimeOffset At, DeploymentLogLevel Level, string Text);

public enum ImagePullPolicy
{
    /// <summary>Pull only images missing on the host (redeploy).</summary>
    Missing,

    /// <summary>Always pull images (redeploy with pull).</summary>
    Always,
}

/// <summary>
/// One run of the deploy pipeline for a stack:
/// Queued → Running → Succeeded | Failed | Cancelled. Finished deployments never change.
/// </summary>
public sealed class Deployment : Entity<DeploymentId>
{
    public const int MaxErrorLength = 2000;
    public const int MaxLogLines = 5000;
    public const int MaxLogLineLength = 2000;

    private readonly List<DeploymentLogLine> log = [];

    private Deployment(
        DeploymentId id,
        StackId stackId,
        DeploymentTrigger trigger,
        UserId? requestedBy,
        ImagePullPolicy pullPolicy,
        DateTimeOffset queuedAt)
        : base(id)
    {
        StackId = stackId;
        Trigger = trigger;
        RequestedBy = requestedBy;
        PullPolicy = pullPolicy;
        QueuedAt = queuedAt;
        Status = DeploymentStatus.Queued;
    }

    // For EF Core.
    private Deployment() { }

    public StackId StackId { get; private init; }

    public DeploymentTrigger Trigger { get; private init; }

    /// <summary>User who started a manual deployment; null when no user did (webhook, deploy hook, rehydration).</summary>
    public UserId? RequestedBy { get; private init; }

    public ImagePullPolicy PullPolicy { get; private init; }

    public DeploymentStatus Status { get; private set; }

    /// <summary>Commit being deployed; known once the pipeline has fetched the repository.</summary>
    public CommitSha? Commit { get; private set; }

    public DateTimeOffset QueuedAt { get; private init; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>Short failure reason. It must not contain secret values; the pipeline masks them first.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// What the pipeline did, in order. Lines must not contain secret values; the pipeline masks them first.
    /// After <see cref="MaxLogLines"/> lines the rest is dropped and one final line says so.
    /// </summary>
    public IReadOnlyList<DeploymentLogLine> Log => [.. log.OrderBy(line => line.Sequence)];

    public bool IsFinished => Status is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Cancelled;

    public TimeSpan? Duration => StartedAt is { } started && FinishedAt is { } finished ? finished - started : null;

    public static Deployment QueueManual(StackId stackId, UserId requestedBy, ImagePullPolicy pullPolicy, DateTimeOffset now) =>
        new(DeploymentId.New(), stackId, DeploymentTrigger.Manual, requestedBy, pullPolicy, now);

    public static Deployment QueueFromWebhook(StackId stackId, DateTimeOffset now) =>
        new(DeploymentId.New(), stackId, DeploymentTrigger.Webhook, requestedBy: null, ImagePullPolicy.Missing, now);

    /// <summary>
    /// Deploys the branch head and pulls every image again: CI has just pushed a new image under a moving tag, which
    /// a deployment pulling only missing images would not see.
    /// </summary>
    public static Deployment QueueFromDeployHook(StackId stackId, DateTimeOffset now) =>
        new(DeploymentId.New(), stackId, DeploymentTrigger.DeployHook, requestedBy: null, ImagePullPolicy.Always, now);

    /// <summary>
    /// Re-deploys <paramref name="commit"/>, the one already running, after the host lost its file secrets in a reboot.
    /// The commit is pinned from the start: the branch may have moved on, and restoring secrets must not deploy
    /// anything nobody asked for. Images are pulled only when missing, as they normally are already there.
    /// </summary>
    public static Deployment QueueRehydrate(StackId stackId, CommitSha commit, DateTimeOffset now) =>
        new(DeploymentId.New(), stackId, DeploymentTrigger.Rehydrate, requestedBy: null, ImagePullPolicy.Missing, now)
        {
            Commit = commit,
        };

    /// <summary>True when the deployment must use <see cref="Commit"/> instead of the branch head.</summary>
    public bool IsPinned => Trigger == DeploymentTrigger.Rehydrate;

    public void Start(DateTimeOffset now)
    {
        TransitionTo(DeploymentStatus.Running, DeploymentStatus.Queued);
        StartedAt = now;
    }

    public void ResolveCommit(CommitSha commit)
    {
        EnsureStatus(DeploymentStatus.Running);
        if (Commit is not null && Commit != commit)
        {
            throw new DomainConflictException($"Deployment {Id} is already pinned to commit {Commit.Short}.");
        }

        Commit = commit;
    }

    public void Succeed(DateTimeOffset now)
    {
        if (Commit is null)
        {
            throw new DomainConflictException($"Deployment {Id} cannot succeed without a resolved commit.");
        }

        TransitionTo(DeploymentStatus.Succeeded, DeploymentStatus.Running);
        FinishedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new DomainValidationException("Failure reason must not be empty.");
        }

        TransitionTo(DeploymentStatus.Failed, DeploymentStatus.Queued, DeploymentStatus.Running);
        Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        FinishedAt = now;
    }

    public void AppendLog(DeploymentLogLevel level, string text, DateTimeOffset now)
    {
        if (IsFinished)
        {
            throw new DomainConflictException($"Deployment {Id} is {Status}; its log is closed.");
        }

        if (log.Count > MaxLogLines)
        {
            return;
        }

        if (log.Count == MaxLogLines)
        {
            log.Add(new DeploymentLogLine(log.Count, now, DeploymentLogLevel.Warning, "Log limit reached; further lines are dropped."));
            return;
        }

        var line = text.Length > MaxLogLineLength ? text[..MaxLogLineLength] : text;
        log.Add(new DeploymentLogLine(log.Count, now, level, line));
    }

    public void Cancel(DateTimeOffset now)
    {
        TransitionTo(DeploymentStatus.Cancelled, DeploymentStatus.Queued, DeploymentStatus.Running);
        FinishedAt = now;
    }

    private void TransitionTo(DeploymentStatus target, params ReadOnlySpan<DeploymentStatus> allowedFrom)
    {
        EnsureStatus(allowedFrom, target);
        Status = target;
    }

    private void EnsureStatus(DeploymentStatus expected) => EnsureStatus([expected], target: null);

    private void EnsureStatus(ReadOnlySpan<DeploymentStatus> allowed, DeploymentStatus? target)
    {
        if (allowed.Contains(Status))
        {
            return;
        }

        var action = target is null ? "this operation" : $"moving to {target}";
        throw new DomainConflictException($"Deployment {Id} is {Status}; {action} is not allowed.");
    }
}
