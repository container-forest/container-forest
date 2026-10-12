using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Audit;

/// <summary>
/// An action about to be recorded. The work it wraps may fill in what it learns on the way: the target once it is
/// loaded, details, or a failure that is a result rather than an exception (a compose run that failed).
/// </summary>
public sealed class AuditDraft(AuditAction action, AuditTarget? target = null)
{
    public AuditAction Action { get; } = action;

    public AuditTarget? Target { get; set; } = target;

    public string? Details { get; set; }

    /// <summary>Set by <see cref="Fail"/>: the work returned normally, but what it did failed.</summary>
    public string? Failure { get; private set; }

    public void Fail(string reason) => Failure = reason;
}

/// <summary>Records what the current user does: who, through what, to what, and how it ended.</summary>
public interface IAuditTrail
{
    /// <summary>
    /// Runs <paramref name="work"/> and records its outcome: succeeded, denied (<see cref="AccessDeniedException"/>) or
    /// failed (any other exception, or <see cref="AuditDraft.Fail"/>). Exceptions are recorded and rethrown.
    /// </summary>
    Task<T> RunAsync<T>(AuditDraft draft, Func<Task<T>> work, CancellationToken cancellationToken);

    /// <summary>Records an action whose outcome the caller already knows, e.g. a sign-in.</summary>
    /// <param name="actingAs">Who acted, when it is not the request's user: a sign-in has no session yet.</param>
    Task RecordAsync(
        AuditAction action, AuditOutcome outcome, AuditTarget? target, string? details, CancellationToken cancellationToken, AuditPerson? actingAs = null);
}

/// <summary>Who acted, for requests whose user is not signed in (yet); the address still comes from the request.</summary>
/// <param name="UserId">Null when the name belongs to no user, e.g. a sign-in attempt with an unknown name.</param>
public sealed record AuditPerson(UserId? UserId, string? UserName, AuditChannel Channel);

public static class AuditTrailExtensions
{
    public static Task RunAsync(this IAuditTrail trail, AuditDraft draft, Func<Task> work, CancellationToken cancellationToken) =>
        trail.RunAsync(draft, async () =>
        {
            await work();
            return true;
        }, cancellationToken);
}

/// <summary>
/// <see cref="IAuditTrail"/> over <see cref="IAuditLog"/>. Recording never breaks the action: an entry that cannot be
/// written is logged as an error instead.
/// </summary>
public sealed partial class AuditTrail(
    ICurrentUser currentUser,
    IUserRepository users,
    IRequestOrigin origin,
    IAuditLog log,
    TimeProvider time,
    ILogger<AuditTrail> logger) : IAuditTrail
{
    public async Task<T> RunAsync<T>(AuditDraft draft, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        T result;
        try
        {
            result = await work();
        }
        catch (UnauthenticatedException)
        {
            // Nobody to attribute it to: unauthenticated requests are turned away before they reach any action.
            throw;
        }
        catch (Exception exception)
        {
            var (outcome, reason) = AuditOutcomes.Of(exception);
            await WriteAsync(draft.Action, outcome, draft.Target, Join(draft.Details, reason), actingAs: null);
            throw;
        }

        await WriteAsync(
            draft.Action,
            draft.Failure is null ? AuditOutcome.Succeeded : AuditOutcome.Failed,
            draft.Target,
            Join(draft.Details, draft.Failure),
            actingAs: null);
        return result;
    }

    public Task RecordAsync(
        AuditAction action, AuditOutcome outcome, AuditTarget? target, string? details, CancellationToken cancellationToken, AuditPerson? actingAs = null) =>
        WriteAsync(action, outcome, target, details, actingAs);

    /// <summary>
    /// Written without the caller's cancellation token: a closed browser tab must not lose the record of what its
    /// request already did.
    /// </summary>
    private async Task WriteAsync(AuditAction action, AuditOutcome outcome, AuditTarget? target, string? details, AuditPerson? actingAs)
    {
        try
        {
            var actor = actingAs is null
                ? await CurrentActorAsync()
                : new AuditActor(actingAs.UserId, actingAs.UserName, actingAs.Channel, null, AddressFor(actingAs.Channel));
            var entry = AuditEntry.Record(actor, action, outcome, target, details, time.GetUtcNow());
            await log.AppendAsync(entry, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogNotRecorded(exception, action, outcome);
        }
    }

    private async Task<AuditActor> CurrentActorAsync()
    {
        var address = AddressFor(origin.IsBot ? AuditChannel.Bot : AuditChannel.Panel);
        if (currentUser.Id is not { } id)
        {
            return new AuditActor(null, null, AuditChannel.Anonymous, null, address);
        }

        var user = await users.FindAsync(id, CancellationToken.None);
        var channel = currentUser.ApiTokenId is not null ? AuditChannel.ApiToken
            : origin.IsBot ? AuditChannel.Bot
            : AuditChannel.Panel;
        return new AuditActor(id, user?.UserName, channel, currentUser.ApiTokenId, address);
    }

    // The bot's address says nothing about the person behind it.
    private string? AddressFor(AuditChannel channel) => channel == AuditChannel.Bot ? null : origin.Address;

    private static string? Join(string? details, string? reason) =>
        (details, reason) switch
        {
            (null, _) => reason,
            (_, null) => details,
            _ => $"{details}. {reason}",
        };

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not write the audit entry for {Action} ({Outcome})")]
    private partial void LogNotRecorded(Exception exception, AuditAction action, AuditOutcome outcome);
}

/// <summary>How an exception reads in the audit log.</summary>
public static class AuditOutcomes
{
    public static (AuditOutcome Outcome, string Reason) Of(Exception exception) => exception switch
    {
        AccessDeniedException denied => (AuditOutcome.Denied, denied.Message),
        NotFoundException notFound => (AuditOutcome.Failed, notFound.Message),
        DomainException domain => (AuditOutcome.Failed, domain.Message),

        // The reasons describe the policy, never the password, but a shorter line is enough here.
        PasswordRejectedException => (AuditOutcome.Failed, "Password rejected."),
        AgentException agent => (AuditOutcome.Failed, agent.Message),

        // Written for the user: which key is missing, never the content.
        SopsDecryptionException or SopsConfigException => (AuditOutcome.Failed, exception.Message),
        OperationCanceledException => (AuditOutcome.Failed, "Cancelled."),

        // Other messages may carry details meant for the aggregator's log only.
        _ => (AuditOutcome.Failed, "Internal error."),
    };
}
