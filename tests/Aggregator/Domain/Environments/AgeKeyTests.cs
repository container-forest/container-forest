using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Environments;
using static ContainerForest.Aggregator.Domain.Tests.TestData;

namespace ContainerForest.Aggregator.Domain.Tests.Environments;

public class AgeKeyTests
{
    public const string Recipient = "age1dhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxng";

    [Fact]
    public void Keeps_the_recipient_and_the_protected_identity()
    {
        var environmentId = EnvironmentId.New();

        var key = AgeKey.Create(environmentId, Recipient, "protected", Now);

        Assert.Equal((environmentId, Recipient, "protected", Now), (key.EnvironmentId, key.Recipient, key.ProtectedIdentity, key.CreatedAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AGE1DHW2UYJ0RK688E62ZMAZ2XFAF2JN46CKKDEY5UT7D8AVPERUFQWQ83NXNG")]
    [InlineData("age1dhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxn")]
    [InlineData("age1dhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxngb")]
    [InlineData("age1bhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxng")]
    [InlineData("age1dhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxng\n")]
    [InlineData("AGE-SECRET-KEY-1QQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQ")]
    public void Rejects_what_is_not_a_recipient(string recipient) =>
        Assert.Throws<DomainValidationException>(() => AgeKey.Create(EnvironmentId.New(), recipient, "protected", Now));

    [Fact]
    public void Requires_the_identity() =>
        Assert.Throws<DomainValidationException>(() => AgeKey.Create(EnvironmentId.New(), Recipient, "", Now));
}
