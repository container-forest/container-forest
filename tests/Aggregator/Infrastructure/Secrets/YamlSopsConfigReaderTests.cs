using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Aggregator.Infrastructure.Secrets;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Secrets;

public sealed class YamlSopsConfigReaderTests
{
    private const string Prod = "age1dhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxng";
    private const string Staging = "age1qyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqs3290gq";
    private const string Recovery = "age1lggyhqrw2nlhcxprm67z43rta597azn8gknawjehu9d9dl0jq3yqqvfafg";

    private static readonly string Config = $"""
        creation_rules:
          - path_regex: ^secrets/prod/
            age: {Prod},{Recovery}
            encrypted_regex: ^(data|stringData)$
          - path_regex: ^secrets/staging/
            age:
              - {Staging}
              - {Recovery}
          - age: {Recovery}
        """;

    private readonly YamlSopsConfigReader reader = new();

    [Theory]
    [InlineData("secrets/prod/db.yaml", new[] { Prod, Recovery })]
    [InlineData("secrets/staging/.env", new[] { Staging, Recovery })]
    [InlineData("apps/web/.env", new[] { Recovery })]
    public void First_matching_rule_applies(string path, string[] recipients) =>
        Assert.Equal(recipients, reader.RuleFor(Config, StackFilePath.Parse(path)).AgeRecipients);

    [Fact]
    public void Keeps_what_to_encrypt() =>
        Assert.Equal("^(data|stringData)$", reader.RuleFor(Config, StackFilePath.Parse("secrets/prod/a.yaml")).EncryptedRegex);

    [Theory]
    [InlineData("creation_rules:\n  - path_regex: ^prod/\n    age: " + Prod + "\n", "No creation rule")]
    [InlineData("creation_rules:\n  - kms: arn:aws:kms:eu-west-1:1:key/x\n", "uses 'kms'")]
    [InlineData("creation_rules:\n  - hc_vault_transit_uri: http://169.254.169.254/\n    age: " + Prod + "\n", "uses 'hc_vault_transit_uri'")]
    [InlineData("creation_rules:\n  - age: ssh-ed25519 AAAA\n", "not an age recipient")]
    [InlineData("creation_rules:\n  - path_regex: (a)\\1\n    age: " + Prod + "\n", "not supported")]
    [InlineData("stores: {}\n", "no creation_rules")]
    [InlineData("creation_rules: [\n", "not valid YAML")]
    public void Unusable_config_is_explained(string config, string reason)
    {
        var error = Assert.Throws<SopsConfigException>(() => reader.RuleFor(config, StackFilePath.Parse("apps/.env")));

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_values_of_other_key_types_are_ignored() =>
        Assert.Equal([Prod], reader.RuleFor($"creation_rules:\n  - pgp: ''\n    age: {Prod}\n", StackFilePath.Parse("a.env")).AgeRecipients);
}
