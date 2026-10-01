using ContainerForest.Aggregator.Infrastructure.Accounts;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Accounts;

public class PasswordRulesTests
{
    [Theory]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("abababababab")]
    [InlineData("abcdabcdabcd")]
    [InlineData("123456789012")]
    [InlineData("987654321098")]
    [InlineData("ABCDEFGHIJKL")]
    [InlineData("lkjihgfedcba")]
    [InlineData("qwertyuiopas")]
    [InlineData("lkjhgfdsapoi")]
    public void Repetitive_or_sequential_passwords_are_rejected(string password) =>
        Assert.NotNull(new RepetitiveOrSequentialRule().Check(password, "alice"));

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("abcdeabcdeab")]
    [InlineData("123456789013")]
    [InlineData("qwertyuiop12")]
    public void Ordinary_passwords_pass_the_repetition_rule(string password) =>
        Assert.Null(new RepetitiveOrSequentialRule().Check(password, "alice"));

    [Theory]
    [InlineData("my name is ALICE, really")]
    [InlineData("i love container-forest")]
    [InlineData("ContainerForest2026!")]
    public void Context_words_are_rejected(string password) =>
        Assert.NotNull(new ContextWordsRule().Check(password, "alice"));

    [Fact]
    public void Short_user_names_are_not_looked_for()
    {
        var rule = new ContextWordsRule();

        Assert.Null(rule.Check("abstract painting", "ab"));
        Assert.Null(rule.Check("a perfectly fine one", null));
    }

    [Fact]
    public void Length_is_capped_but_long_passphrases_pass()
    {
        var rule = new MaximumLengthRule();

        Assert.Null(rule.Check(new string('x', MaximumLengthRule.MaxLength), null));
        Assert.NotNull(rule.Check(new string('x', MaximumLengthRule.MaxLength + 1), null));
    }

    [Fact]
    public async Task A_failed_rule_becomes_a_password_error_for_identity()
    {
        var user = new AccountUser(Domain.Access.User.Create("alice", DateTimeOffset.UnixEpoch));

        var result = await new ContextWordsRule().ValidateAsync(null!, user, "alice in wonderland");

        var error = Assert.Single(result.Errors);
        Assert.StartsWith("Password", error.Code, StringComparison.Ordinal);
        Assert.Equal("Passwords must not contain the user name.", error.Description);
        Assert.True((await new ContextWordsRule().ValidateAsync(null!, user, "a passphrase of my own")).Succeeded);
    }
}
