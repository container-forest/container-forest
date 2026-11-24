using System.Text;
using ContainerForest.Aggregator.Infrastructure.Accounts;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Accounts;

public class TotpAuthenticatorTests
{
    /// <summary>The SHA-1 secret of RFC 6238, appendix B.</summary>
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Codes_match_the_rfc_test_vectors(long unixSeconds, string expected) =>
        Assert.Equal(expected, TotpAuthenticator.Compute(Secret, TotpAuthenticator.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))));

    [Fact]
    public void Base32_round_trips_and_refuses_other_characters()
    {
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Base32.Encode(Secret));
        Assert.Equal(Secret, Base32.Decode("gezd gnbv gy3t qojq gezd gnbv gy3t qojq"));
        Assert.Null(Base32.Decode("GEZDGNB1"));
    }

    [Theory]
    [InlineData("123456", true)]
    [InlineData("123 456", true)]
    [InlineData("12345", false)]
    [InlineData("12345a", false)]
    [InlineData("ABCDE-FGHIJ", false)]
    public void Only_six_digits_are_an_app_code(string token, bool valid) =>
        Assert.Equal(valid, TotpAuthenticator.TryParseCode(token, out _));

    [Fact]
    public void Uri_names_the_issuer_and_the_account()
    {
        var uri = TotpAuthenticator.Uri("container-forest", "alice@panel.example.com", "ABC");

        Assert.Equal("otpauth://totp/container-forest:alice%40panel.example.com?secret=ABC&issuer=container-forest&digits=6&period=30", uri);
    }
}
