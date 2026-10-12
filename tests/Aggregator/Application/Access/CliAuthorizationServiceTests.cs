using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;

namespace ContainerForest.Aggregator.Application.Tests.Access;

public class CliAuthorizationServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryUsers users = new();
    private readonly InMemoryApiTokens tokens = new();
    private readonly InMemoryCliAuthorizations authorizations = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly InMemoryEnvironments environments = new();
    private readonly FakeCurrentUser currentUser = new();
    private readonly FakeUnitOfWork unitOfWork = new();
    private readonly MovableTime time = new(Start);

    private readonly User alice = User.Create("alice", Start);
    private readonly DockerEnvironment privateEnvironment = DockerEnvironment.Create("private", isPrivate: true, Start);

    public CliAuthorizationServiceTests()
    {
        users.Add(alice);
        environments.Add(privateEnvironment);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Approved_login_hands_out_the_token_once()
    {
        var started = await Anonymous().StartAsync("forest", "laptop", "203.0.113.7", Ct);
        Assert.Equal(CliPollResult.Pending, (await Anonymous().ExchangeAsync(started.DeviceCode, Ct)).Status);

        await As(alice).ApproveAsync(started.UserCode, "forest on laptop", false, null, null, Ct);
        time.Now += CliAuthorization.PollInterval;
        var issued = await Anonymous().ExchangeAsync(started.DeviceCode, Ct);

        Assert.Equal(CliPollResult.Approved, issued.Status);
        Assert.Equal("alice", issued.UserName);
        Assert.NotNull(await new ApiTokenAuthenticator(tokens, unitOfWork, time).AuthenticateAsync(issued.Secret!, Ct));
        Assert.Equal(CliPollResult.Expired, (await Anonymous().ExchangeAsync(started.DeviceCode, Ct)).Status);
        Assert.Equal(0, authorizations.Count);
    }

    [Fact]
    public async Task Denied_login_gets_no_token_and_is_forgotten()
    {
        var started = await Anonymous().StartAsync("forest", "laptop", null, Ct);

        await As(alice).DenyAsync(started.UserCode, Ct);
        var result = await Anonymous().ExchangeAsync(started.DeviceCode, Ct);

        Assert.Equal(CliPollResult.Denied, result.Status);
        Assert.Null(result.Secret);
        Assert.Equal(0, authorizations.Count);
    }

    [Fact]
    public async Task Unknown_device_code_looks_expired() =>
        Assert.Equal(CliPollResult.Expired, (await Anonymous().ExchangeAsync("nope", Ct)).Status);

    [Fact]
    public async Task Token_cannot_approve_a_login()
    {
        var started = await Anonymous().StartAsync("forest", "laptop", null, Ct);
        var service = As(alice);
        currentUser.UseToken(ApiToken.Create(alice.Id, "cli", false, null, null, Start).Token);

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ApproveAsync(started.UserCode, "x", false, null, null, Ct));
    }

    [Fact]
    public async Task Approval_limits_the_token_to_visible_environments_only()
    {
        var started = await Anonymous().StartAsync("forest", "laptop", null, Ct);

        await Assert.ThrowsAsync<NotFoundException>(
            () => As(alice).ApproveAsync(started.UserCode, "x", false, [privateEnvironment.Id], null, Ct));
    }

    [Fact]
    public async Task Expired_code_is_not_found()
    {
        var started = await Anonymous().StartAsync("forest", "laptop", null, Ct);
        time.Now += CliAuthorization.Lifetime;

        await Assert.ThrowsAsync<NotFoundException>(() => As(alice).GetOpenAsync(started.UserCode, Ct));
    }

    [Fact]
    public async Task User_deactivated_after_approving_gets_no_token()
    {
        var started = await Anonymous().StartAsync("forest", "laptop", null, Ct);
        await As(alice).ApproveAsync(started.UserCode, "x", false, null, null, Ct);
        alice.Deactivate();

        var result = await Anonymous().ExchangeAsync(started.DeviceCode, Ct);

        Assert.Equal(CliPollResult.Denied, result.Status);
        Assert.Null(result.Secret);
    }

    [Fact]
    public async Task Expired_requests_are_cleaned_up_when_a_new_one_starts()
    {
        await Anonymous().StartAsync("forest", "old", null, Ct);
        time.Now += CliAuthorization.Lifetime;

        await Anonymous().StartAsync("forest", "new", null, Ct);

        Assert.Equal(1, authorizations.Count);
    }

    private CliAuthorizationService Anonymous()
    {
        currentUser.Id = null;
        currentUser.Scope = AccessScope.Full;
        currentUser.ApiTokenId = null;
        return Service();
    }

    private CliAuthorizationService As(User user)
    {
        currentUser.Id = user.Id;
        currentUser.Scope = AccessScope.Full;
        currentUser.ApiTokenId = null;
        return Service();
    }

    private CliAuthorizationService Service()
    {
        var actor = new CurrentActor(currentUser, users);
        var apiTokens = new ApiTokenService(actor, tokens, users, roleAssignments, environments, unitOfWork, new FakeAuditTrail(), time);
        return new CliAuthorizationService(actor, authorizations, tokens, users, apiTokens, unitOfWork, time);
    }

    private sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
