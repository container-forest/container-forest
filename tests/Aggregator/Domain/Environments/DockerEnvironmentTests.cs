using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Environments;

public class DockerEnvironmentTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    [Fact]
    public void Enrolls_with_a_valid_token_and_stores_only_hashes()
    {
        var environment = Environment();
        var token = environment.IssueEnrollmentToken(Now, Lifetime);

        Assert.True(environment.TryEnroll(token, Now.AddMinutes(5), out var secret));

        Assert.True(environment.IsEnrolled);
        Assert.True(environment.VerifyAgentSecret(secret));
        Assert.NotEqual(secret, environment.AgentSecretHash);
        Assert.Null(environment.EnrollmentTokenHash);
    }

    [Fact]
    public void Enrollment_token_is_single_use()
    {
        var environment = Environment();
        var token = environment.IssueEnrollmentToken(Now, Lifetime);
        environment.TryEnroll(token, Now, out _);

        Assert.False(environment.TryEnroll(token, Now, out _));
    }

    [Fact]
    public void Rejects_an_expired_token()
    {
        var environment = Environment();
        var token = environment.IssueEnrollmentToken(Now, Lifetime);

        Assert.False(environment.TryEnroll(token, Now + Lifetime, out _));
        Assert.False(environment.IsEnrolled);
    }

    [Fact]
    public void Rejects_a_wrong_token_and_a_replaced_token()
    {
        var environment = Environment();
        var first = environment.IssueEnrollmentToken(Now, Lifetime);
        environment.IssueEnrollmentToken(Now, Lifetime);

        Assert.False(environment.TryEnroll("wrong", Now, out _));
        Assert.False(environment.TryEnroll(first, Now, out _));
    }

    [Fact]
    public void Re_enrolling_invalidates_the_previous_agent_secret()
    {
        var environment = Environment();
        environment.TryEnroll(environment.IssueEnrollmentToken(Now, Lifetime), Now, out var oldSecret);
        environment.TryEnroll(environment.IssueEnrollmentToken(Now, Lifetime), Now, out var newSecret);

        Assert.False(environment.VerifyAgentSecret(oldSecret!));
        Assert.True(environment.VerifyAgentSecret(newSecret!));
    }

    [Fact]
    public void Detects_a_host_reboot_by_boot_id()
    {
        var environment = EnrolledEnvironment();

        Assert.False(environment.RecordConnection("boot-1", "1.0.0", Now));
        Assert.False(environment.RecordConnection("boot-1", "1.0.0", Now.AddMinutes(1)));
        Assert.True(environment.RecordConnection("boot-2", "1.0.0", Now.AddMinutes(2)));
    }

    [Fact]
    public void Last_seen_never_moves_back()
    {
        var environment = EnrolledEnvironment();
        environment.RecordHeartbeat(Now.AddMinutes(1));
        environment.RecordHeartbeat(Now);

        Assert.Equal(Now.AddMinutes(1), environment.LastSeenAt);
    }

    [Fact]
    public void Connection_without_enrollment_is_a_conflict() =>
        Assert.Throws<DomainConflictException>(() => Environment().RecordConnection("boot-1", "1.0.0", Now));

    [Fact]
    public void Revoked_agent_cannot_authenticate()
    {
        var environment = Environment();
        environment.TryEnroll(environment.IssueEnrollmentToken(Now, Lifetime), Now, out var secret);
        environment.RevokeAgent();

        Assert.False(environment.VerifyAgentSecret(secret!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    public void Rejects_invalid_names(string name) =>
        Assert.Throws<DomainValidationException>(() => DockerEnvironment.Create(name, isPrivate: false, Now));

    private static DockerEnvironment EnrolledEnvironment()
    {
        var environment = Environment();
        environment.TryEnroll(environment.IssueEnrollmentToken(Now, Lifetime), Now, out _);
        return environment;
    }
}
