using ContainerForest.Cli.Configuration;

namespace ContainerForest.Cli.Tests.Configuration;

public sealed class CredentialResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"forest-credentials-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> variables = [];
    private readonly ConfigStore store;

    public CredentialResolverTests()
    {
        store = new ConfigStore(root);
        var config = new CliConfig { CurrentContext = "main" };
        config.Contexts["main"] = new ContextEntry { Url = "https://forest.example.com/", Token = "cf_pat_main", Environment = "r2" };
        config.Contexts["other"] = new ContextEntry { Url = "https://other.example.com/", Token = "cf_pat_other" };
        store.Save(config);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Current_context_gives_server_token_and_environment()
    {
        var connection = Resolve(new CredentialOptions(null, null, null, null));

        Assert.Equal("https://forest.example.com/", connection.Url.ToString());
        Assert.Equal("cf_pat_main", connection.Token);
        Assert.Equal(CredentialSource.Context, connection.Source);
        Assert.Equal("r2", connection.DefaultEnvironment);
    }

    [Fact]
    public void Named_context_wins_over_the_current_one()
    {
        variables["CF_CONTEXT"] = "other";

        Assert.Equal("cf_pat_other", Resolve(new CredentialOptions(null, null, null, null)).Token);
        Assert.Equal("cf_pat_main", Resolve(new CredentialOptions(null, null, null, "main")).Token);
    }

    [Fact]
    public void Option_wins_over_the_variable_and_the_context()
    {
        variables["CF_TOKEN"] = "cf_pat_variable";

        Assert.Equal("cf_pat_variable", Resolve(new CredentialOptions(null, null, null, null)).Token);
        var connection = Resolve(new CredentialOptions(null, "cf_pat_option", null, null));
        Assert.Equal("cf_pat_option", connection.Token);
        Assert.Equal(CredentialSource.Option, connection.Source);
    }

    [Fact]
    public void Token_is_read_from_a_file_or_standard_input()
    {
        var file = Path.Combine(root, "token");
        File.WriteAllText(file, "cf_pat_file\n");

        Assert.Equal("cf_pat_file", Resolve(new CredentialOptions(null, null, file, null)).Token);
        Assert.Equal("cf_pat_stdin", Resolve(new CredentialOptions(null, null, "-", null), "cf_pat_stdin\n").Token);
    }

    [Fact]
    public void Contexts_token_never_goes_to_another_server()
    {
        var refused = Assert.Throws<CliException>(() => Resolve(new CredentialOptions("https://evil.example.com", null, null, null)));

        Assert.Equal(ExitCodes.Unauthenticated, refused.ExitCode);
        Assert.Contains("evil.example.com", refused.Message);
    }

    [Fact]
    public void Same_server_by_url_keeps_the_contexts_token()
    {
        variables["CF_URL"] = "forest.example.com";

        Assert.Equal("cf_pat_main", Resolve(new CredentialOptions(null, null, null, null)).Token);
    }

    [Fact]
    public void Unknown_named_context_is_an_error() =>
        Assert.Equal(ExitCodes.Usage, Assert.Throws<CliException>(() => Resolve(new CredentialOptions(null, null, null, "nope"))).ExitCode);

    [Fact]
    public void No_server_at_all_asks_to_log_in()
    {
        var empty = new ConfigStore(Path.Combine(root, "empty"));
        var resolver = new CredentialResolver(empty, variables.GetValueOrDefault, new StringReader(""));

        var refused = Assert.Throws<CliException>(() => resolver.Resolve(new CredentialOptions(null, null, null, null)));

        Assert.Contains("forest login", refused.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cf_pat_a b")]
    public void Empty_or_spaced_token_is_refused(string token) =>
        Assert.Throws<CliException>(() => Resolve(new CredentialOptions(null, token, null, null)));

    [Fact]
    public void Connection_does_not_print_its_token() =>
        Assert.DoesNotContain("cf_pat_main", Resolve(new CredentialOptions(null, null, null, null)).ToString());

    private Connection Resolve(CredentialOptions options, string input = "") =>
        new CredentialResolver(store, variables.GetValueOrDefault, new StringReader(input)).Resolve(options);
}
