using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Aggregator.Application.Tests.Audit;

public class AuditTrailTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUsers users = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly FakeOrigin origin = new();
    private readonly MemoryAuditLog log = new();
    private readonly User alice = User.Create("alice", Now);
    private readonly AuditTrail trail;

    public AuditTrailTests()
    {
        users.Add(alice);
        currentUser.Id = alice.Id;
        trail = new AuditTrail(currentUser, users, origin, log, new Deployments.FixedTime(Now), NullLogger<AuditTrail>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Successful_work_is_recorded_with_the_target_it_found()
    {
        var draft = new AuditDraft(AuditAction.UserRenamed);

        var result = await trail.RunAsync(draft, () =>
        {
            draft.Target = new UserAuditTarget(alice.Id, "alice");
            return Task.FromResult(42);
        }, Ct);

        Assert.Equal(42, result);
        var entry = Assert.Single(log.Entries);
        Assert.Equal((AuditAction.UserRenamed, AuditOutcome.Succeeded, "alice"), (entry.Action, entry.Outcome, entry.TargetName));
        Assert.Equal((alice.Id, "alice", AuditChannel.Panel, "198.51.100.1"), (entry.ActorId, entry.ActorName, entry.Channel, entry.Address));
    }

    [Fact]
    public async Task Denied_work_is_recorded_and_rethrown()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => trail.RunAsync(
            new AuditDraft(AuditAction.UserCreated), () => Task.FromException<int>(new AccessDeniedException("Only administrators may do this.")), Ct));

        var entry = Assert.Single(log.Entries);
        Assert.Equal((AuditOutcome.Denied, "Only administrators may do this."), (entry.Outcome, entry.Details));
    }

    [Fact]
    public async Task Unexpected_errors_are_recorded_without_their_message()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => trail.RunAsync(
            new AuditDraft(AuditAction.StackCreated) { Details = "main" },
            () => Task.FromException<int>(new InvalidOperationException("connection string with a password")),
            Ct));

        var entry = Assert.Single(log.Entries);
        Assert.Equal((AuditOutcome.Failed, "main. Internal error."), (entry.Outcome, entry.Details));
    }

    [Fact]
    public async Task A_failure_reported_by_the_work_is_recorded_as_failed()
    {
        var draft = new AuditDraft(AuditAction.StackTakenDown);

        await trail.RunAsync(draft, () =>
        {
            draft.Fail("compose exited with code 1");
            return Task.FromResult(true);
        }, Ct);

        Assert.Equal((AuditOutcome.Failed, "compose exited with code 1"), (log.Entries[0].Outcome, log.Entries[0].Details));
    }

    [Fact]
    public async Task Unauthenticated_requests_are_not_recorded()
    {
        await Assert.ThrowsAsync<UnauthenticatedException>(() => trail.RunAsync(
            new AuditDraft(AuditAction.ContainerStarted), () => Task.FromException<int>(new UnauthenticatedException()), Ct));

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Token_and_bot_requests_say_how_they_came()
    {
        var tokenId = ApiTokenId.New();
        currentUser.ApiTokenId = tokenId;
        await trail.RecordAsync(AuditAction.DeploymentQueued, AuditOutcome.Succeeded, null, null, Ct);

        currentUser.ApiTokenId = null;
        origin.IsBot = true;
        await trail.RecordAsync(AuditAction.ContainerRestarted, AuditOutcome.Succeeded, null, null, Ct);

        Assert.Equal((AuditChannel.ApiToken, tokenId), (log.Entries[0].Channel, log.Entries[0].ApiTokenId));
        Assert.Equal((AuditChannel.Bot, null), (log.Entries[1].Channel, log.Entries[1].Address));
    }

    [Fact]
    public async Task A_sign_in_names_its_user_before_there_is_a_session()
    {
        currentUser.Id = null;

        await trail.RecordAsync(
            AuditAction.SignInFailed, AuditOutcome.Failed, null, "Wrong password.", Ct, new AuditPerson(null, "mallory", AuditChannel.Anonymous));

        var entry = Assert.Single(log.Entries);
        Assert.Equal((null, "mallory", AuditChannel.Anonymous, "198.51.100.1"), (entry.ActorId, entry.ActorName, entry.Channel, entry.Address));
    }

    [Fact]
    public async Task A_log_that_cannot_be_written_does_not_break_the_action()
    {
        log.Broken = true;

        var result = await trail.RunAsync(new AuditDraft(AuditAction.SignedOut), () => Task.FromResult("done"), Ct);

        Assert.Equal("done", result);
    }

    private sealed class FakeOrigin : IRequestOrigin
    {
        public bool IsBot { get; set; }

        public string? Address => "198.51.100.1";
    }

    private sealed class MemoryAuditLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public bool Broken { get; set; }

        public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            if (Broken)
            {
                throw new InvalidOperationException("disk full");
            }

            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<AuditPage> ListAsync(AuditFilter filter, int skip, int take, CancellationToken cancellationToken) =>
            Task.FromResult(new AuditPage(Entries, Entries.Count));

        public Task<int> RemoveOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
            Task.FromResult(Entries.RemoveAll(e => e.OccurredAt < cutoff));
    }
}
