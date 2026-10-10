using ContainerForest.Cli.Secrets;

namespace ContainerForest.Cli.Tests.Secrets;

public class SopsConfigFileTests
{
    // As 'forest secrets init --rule stacks/prod/=prod -e dev --recovery age1rec' writes it.
    private const string Init = """
        # Written by 'forest secrets init' (ADR-0010): the first rule whose path_regex matches a file applies.
        creation_rules:
          # environment prod
          - path_regex: '^stacks/prod/'
            age: age1prod,age1rec
          # environment dev
          - age: age1dev,age1rec

        """;

    [Fact]
    public void Unchanged_file_is_written_back_as_it_was()
    {
        var config = SopsConfigFile.Parse(Init);

        Assert.Equal(Init, config.ToString());
        Assert.Equal(["^stacks/prod/", null], config.Rules.Select(r => r.PathRegex));
        Assert.Equal(["age1dev", "age1rec"], config.Rules[1].AgeRecipients);
    }

    [Fact]
    public void Prefix_rule_goes_before_the_catch_all()
    {
        var config = SopsConfigFile.Parse(Init);

        var position = config.AddPrefixRule("stacks/staging/", ["staging"], ["age1staging", "age1rec"]);

        Assert.Equal(1, position);
        Assert.Equal("""
            # Written by 'forest secrets init' (ADR-0010): the first rule whose path_regex matches a file applies.
            creation_rules:
              # environment prod
              - path_regex: '^stacks/prod/'
                age: age1prod,age1rec
              # environment staging
              - path_regex: '^stacks/staging/'
                age: age1staging,age1rec
              # environment dev
              - age: age1dev,age1rec

            """, config.ToString());
    }

    [Fact]
    public void Narrower_prefix_goes_before_a_broader_one_and_a_broader_before_a_narrower()
    {
        var config = SopsConfigFile.Parse(Init);

        Assert.Equal(0, config.AddPrefixRule("stacks/prod/db/", ["db"], ["age1db"]));
        Assert.Equal(0, config.AddPrefixRule("stacks/", ["all"], ["age1all"]));
    }

    [Fact]
    public void Rule_for_the_same_prefix_is_refused()
    {
        var config = SopsConfigFile.Parse(Init);

        Assert.Throws<CliException>(() => config.AddPrefixRule("/stacks/prod/", ["prod"], ["age1prod"]));
    }

    [Fact]
    public void Removing_a_rule_takes_its_comment_with_it()
    {
        var config = SopsConfigFile.Parse(Init);

        config.RemovePrefixRule("stacks/prod/");

        Assert.Equal("""
            # Written by 'forest secrets init' (ADR-0010): the first rule whose path_regex matches a file applies.
            creation_rules:
              # environment dev
              - age: age1dev,age1rec

            """, config.ToString());
        Assert.Throws<CliException>(() => config.RemovePrefixRule("stacks/prod/"));
    }

    [Fact]
    public void Hand_written_rules_keep_their_keys_and_trailing_comments()
    {
        var config = SopsConfigFile.Parse("""
            creation_rules:
            - path_regex: \.dev\.yaml$
              encrypted_regex: ^(data|stringData)$
              age: age1one
            - path_regex: "^prod/"
              pgp: ABCDEF
              age: age1two # ops laptop
            stores:
              yaml:
                indent: 2
            """);

        Assert.Equal(["^prod/"], config.Rules.Skip(1).Select(r => r.PathRegex));
        Assert.Equal(["age1two"], config.Rules[1].AgeRecipients);
        Assert.Equal(["pgp"], config.Rules[1].OtherKeyTypes);
        Assert.EndsWith("stores:\n  yaml:\n    indent: 2\n", config.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("keys: []\n")]
    [InlineData("creation_rules: [{age: age1x}]\n")]
    [InlineData("creation_rules:\n  - age:\n      - age1x\n")]
    [InlineData("creation_rules:\n  - age: >-\n      age1x\n")]
    public void Forms_it_cannot_edit_are_refused(string text) =>
        Assert.Throws<CliException>(() => SopsConfigFile.Parse(text));

    [Theory]
    [InlineData("^stacks/prod/", "stacks/prod/")]
    [InlineData(@"^a\.b/", "a.b/")]
    [InlineData(@"\.env$", null)]
    [InlineData("^stacks/.*", null)]
    public void Prefix_of_a_path_regex(string pathRegex, string? prefix) =>
        Assert.Equal(prefix, SopsConfigFile.Rule.AsPrefix(pathRegex));
}
