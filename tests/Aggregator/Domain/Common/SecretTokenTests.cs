using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Tests.Common;

public class SecretTokenTests
{
    [Fact]
    public void Generated_tokens_are_unique_and_url_safe()
    {
        var first = SecretToken.Generate();
        var second = SecretToken.Generate();

        Assert.NotEqual(first, second);
        Assert.DoesNotContain(first, c => c is '+' or '/' or '=');
    }

    [Fact]
    public void Matches_only_the_original_token()
    {
        var token = SecretToken.Generate();
        var hash = SecretToken.Hash(token);

        Assert.True(SecretToken.Matches(token, hash));
        Assert.False(SecretToken.Matches(token + "x", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("abcd")]
    public void Malformed_hash_never_matches(string hash) => Assert.False(SecretToken.Matches("token", hash));
}
