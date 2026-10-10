using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Access;

public class ApiTokenTests
{
    [Fact]
    public void Keeps_only_the_hash_and_a_hint_of_the_secret()
    {
        var (token, secret) = ApiToken.Create(UserId.New(), "laptop", readOnly: false, null, null, Now);

        Assert.StartsWith(ApiToken.Prefix, secret);
        Assert.True(ApiToken.HasTokenShape(secret));
        Assert.Equal(ApiToken.HashSecret(secret), token.TokenHash);
        Assert.Equal(secret[..(ApiToken.Prefix.Length + ApiToken.HintLength)], token.Hint);
        Assert.DoesNotContain(secret, token.Hint + token.TokenHash + token.Name);
    }

    [Fact]
    public void Unrestricted_token_has_a_full_scope()
    {
        var (token, _) = ApiToken.Create(UserId.New(), "laptop", readOnly: false, null, null, Now);

        Assert.False(token.Scope.IsRestricted);
        Assert.False(token.Scope.IsReadOnly);
        Assert.False(token.Scope.CanDecryptSecrets);
    }

    [Fact]
    public void Decrypting_secrets_is_a_right_given_explicitly_and_never_to_a_read_only_token()
    {
        var (token, _) = ApiToken.Create(UserId.New(), "git", readOnly: false, null, null, Now, canDecryptSecrets: true);

        Assert.True(token.CanDecryptSecrets);
        Assert.True(token.Scope.CanDecryptSecrets);
        Assert.True(AccessScope.Full.CanDecryptSecrets);
        Assert.Throws<DomainValidationException>(() =>
            ApiToken.Create(UserId.New(), "git", readOnly: true, null, null, Now, canDecryptSecrets: true));
    }

    [Fact]
    public void Restricted_token_reaches_only_its_environments()
    {
        var environmentId = EnvironmentId.New();
        var (token, _) = ApiToken.Create(UserId.New(), "ci", readOnly: true, [environmentId, environmentId], null, Now);

        Assert.True(token.IsRestricted);
        Assert.Equal([environmentId], token.Environments);
        Assert.True(token.Scope.Reaches(environmentId));
        Assert.False(token.Scope.Reaches(EnvironmentId.New()));
        Assert.True(token.Scope.IsReadOnly);
    }

    [Fact]
    public void Restricted_token_needs_an_environment() =>
        Assert.Throws<DomainValidationException>(() => ApiToken.Create(UserId.New(), "ci", false, [], null, Now));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_is_required(string name) =>
        Assert.Throws<DomainValidationException>(() => ApiToken.Create(UserId.New(), name, false, null, null, Now));

    [Fact]
    public void Name_is_trimmed_and_loses_control_characters()
    {
        var (token, _) = ApiToken.Create(UserId.New(), " ci\n runner ", false, null, null, Now);

        Assert.Equal("ci runner", token.Name);
    }

    [Fact]
    public void Expiry_must_be_in_the_future() =>
        Assert.Throws<DomainValidationException>(() => ApiToken.Create(UserId.New(), "ci", false, null, Now, Now));

    [Fact]
    public void Expires_at_its_expiry()
    {
        var (token, _) = ApiToken.Create(UserId.New(), "ci", false, null, Now.AddDays(1), Now);

        Assert.False(token.IsExpired(Now.AddHours(23)));
        Assert.True(token.IsExpired(Now.AddDays(1)));
    }

    [Fact]
    public void Use_is_recorded_at_most_once_a_minute()
    {
        var (token, _) = ApiToken.Create(UserId.New(), "ci", false, null, null, Now);

        Assert.True(token.RecordUse(Now));
        Assert.False(token.RecordUse(Now.AddSeconds(30)));
        Assert.True(token.RecordUse(Now.AddMinutes(1)));
        Assert.Equal(Now.AddMinutes(1), token.LastUsedAt);
    }

    [Fact]
    public void Secrets_differ_between_tokens()
    {
        var first = ApiToken.Create(UserId.New(), "a", false, null, null, Now).Secret;
        var second = ApiToken.Create(UserId.New(), "b", false, null, null, Now).Secret;

        Assert.NotEqual(first, second);
    }
}
