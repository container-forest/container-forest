using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Access;

public class CliAuthorizationTests
{
    [Fact]
    public void Keeps_only_hashes_of_both_codes()
    {
        var (authorization, deviceCode, userCode) = CliAuthorization.Start("forest 0.1", "laptop", "203.0.113.7", Now);

        Assert.Equal(CliAuthorization.HashDeviceCode(deviceCode), authorization.DeviceCodeHash);
        Assert.Equal(CliAuthorization.HashUserCode(userCode), authorization.UserCodeHash);
        Assert.Equal(CliAuthorization.UserCodeLength, userCode.Length);
        Assert.Equal(Now + CliAuthorization.Lifetime, authorization.ExpiresAt);
    }

    [Fact]
    public void User_code_is_typed_in_any_case_and_with_a_dash()
    {
        var (authorization, _, userCode) = CliAuthorization.Start("forest", "laptop", null, Now);
        var shown = CliAuthorization.FormatUserCode(userCode);

        Assert.Equal(9, shown.Length);
        Assert.Equal('-', shown[4]);
        Assert.Equal(authorization.UserCodeHash, CliAuthorization.HashUserCode(shown.ToLowerInvariant()));
    }

    [Fact]
    public void Names_from_the_client_are_cleaned_and_cut()
    {
        var (authorization, _, _) = CliAuthorization.Start(" forest\n ", new string('h', 300), null, Now);

        Assert.Equal("forest", authorization.ClientName);
        Assert.Equal(CliAuthorization.MaxHostNameLength, authorization.HostName.Length);
    }

    [Fact]
    public void Polling_reports_pending_until_approved_then_the_token_is_issued()
    {
        var (authorization, _, _) = CliAuthorization.Start("forest", "laptop", null, Now);
        var environmentId = EnvironmentId.New();
        var userId = UserId.New();

        Assert.Equal(CliPollResult.Pending, authorization.Poll(Now));
        authorization.Approve(userId, "forest on laptop", readOnly: true, [environmentId], Now.AddDays(30), Now.AddSeconds(10));
        var status = authorization.Poll(Now.AddSeconds(10));
        var (token, secret) = authorization.IssueToken(Now.AddSeconds(10));

        Assert.Equal(CliPollResult.Approved, status);
        Assert.Equal(userId, token.UserId);
        Assert.Equal("forest on laptop", token.Name);
        Assert.True(token.IsReadOnly);
        Assert.Equal([environmentId], token.Environments);
        Assert.Equal(Now.AddDays(30), token.ExpiresAt);
        Assert.Equal(ApiToken.HashSecret(secret), token.TokenHash);
    }

    [Fact]
    public void Polling_too_often_is_told_to_slow_down()
    {
        var (authorization, _, _) = CliAuthorization.Start("forest", "laptop", null, Now);

        authorization.Poll(Now);

        Assert.Equal(CliPollResult.SlowDown, authorization.Poll(Now.AddSeconds(1)));
        Assert.Equal(CliPollResult.Pending, authorization.Poll(Now + CliAuthorization.PollInterval));
    }

    [Fact]
    public void Denied_request_reports_denied_and_issues_nothing()
    {
        var (authorization, _, _) = CliAuthorization.Start("forest", "laptop", null, Now);

        authorization.Deny(Now);

        Assert.Equal(CliPollResult.Denied, authorization.Poll(Now));
        Assert.Throws<DomainConflictException>(() => authorization.IssueToken(Now));
    }

    [Fact]
    public void Expired_request_can_be_neither_approved_nor_polled()
    {
        var (authorization, _, _) = CliAuthorization.Start("forest", "laptop", null, Now);
        var later = Now + CliAuthorization.Lifetime;

        Assert.False(authorization.IsOpen(later));
        Assert.Equal(CliPollResult.Expired, authorization.Poll(later));
        Assert.Throws<DomainConflictException>(() => authorization.Approve(UserId.New(), "t", false, null, null, later));
    }

    [Fact]
    public void Request_is_decided_only_once()
    {
        var (authorization, _, _) = CliAuthorization.Start("forest", "laptop", null, Now);
        authorization.Approve(UserId.New(), "t", false, null, null, Now);

        Assert.Throws<DomainConflictException>(() => authorization.Deny(Now));
        Assert.Throws<DomainConflictException>(() => authorization.Approve(UserId.New(), "t", false, null, null, Now));
    }

    [Fact]
    public void Approval_checks_the_token_options()
    {
        var (authorization, _, _) = CliAuthorization.Start("forest", "laptop", null, Now);

        Assert.Throws<DomainValidationException>(() => authorization.Approve(UserId.New(), " ", false, null, null, Now));
        Assert.Throws<DomainValidationException>(() => authorization.Approve(UserId.New(), "t", false, [], null, Now));
        Assert.Throws<DomainValidationException>(() => authorization.Approve(UserId.New(), "t", false, null, Now, Now));
        Assert.True(authorization.IsOpen(Now));
    }

    [Fact]
    public void Approved_token_may_decrypt_secrets_when_the_person_chooses_so()
    {
        var (authorization, _, _) = CliAuthorization.Start("forest", "laptop", null, Now, secretsRequested: true);
        Assert.True(authorization.SecretsRequested);

        authorization.Approve(UserId.New(), "forest", readOnly: false, null, null, Now, canDecryptSecrets: true);

        Assert.True(authorization.IssueToken(Now).Token.CanDecryptSecrets);
    }
}
