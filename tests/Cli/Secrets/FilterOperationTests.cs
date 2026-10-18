using System.Text;
using ContainerForest.Cli.Secrets;

namespace ContainerForest.Cli.Tests.Secrets;

public sealed class FilterOperationTests : IDisposable
{
    private readonly string gitDirectory = Directory.CreateTempSubdirectory("cf-filter-").FullName;
    private readonly FakeSops sops = new();
    private readonly StringWriter error = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(gitDirectory, recursive: true);

    [Fact]
    public async Task Unchanged_plain_text_cleans_to_the_ciphertext_it_came_from()
    {
        var services = Services(FilterCache.Open(gitDirectory));
        var ciphertext = FakeSops.Wrap("TOKEN=s3cret");

        var plain = await new SmudgeOperation(services).RunAsync(".env", ciphertext, Ct);
        services.Cache!.Save();
        var reopened = Services(FilterCache.Open(gitDirectory));
        var cleaned = await new CleanOperation(reopened).RunAsync(".env", plain.Content, Ct);

        Assert.Equal(ciphertext, cleaned.Content);
        Assert.Empty(sops.Encrypted);
        Assert.DoesNotContain("s3cret", await File.ReadAllTextAsync(Path.Combine(gitDirectory, "forest", "cache.json"), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changed_plain_text_or_another_file_is_encrypted_afresh()
    {
        var services = Services(FilterCache.Open(gitDirectory));
        await new SmudgeOperation(services).RunAsync(".env", FakeSops.Wrap("TOKEN=s3cret"), Ct);

        await new CleanOperation(services).RunAsync(".env", "TOKEN=other"u8.ToArray(), Ct);
        await new CleanOperation(services).RunAsync("b/.env", "TOKEN=s3cret"u8.ToArray(), Ct);

        Assert.Equal([".env", "b/.env"], sops.Encrypted);
    }

    [Fact]
    public async Task Encrypted_file_passes_clean_as_it_is()
    {
        var ciphertext = FakeSops.Wrap("TOKEN=x");

        var result = await new CleanOperation(Services(null)).RunAsync(".env", ciphertext, Ct);

        Assert.Equal(ciphertext, result.Content);
        Assert.Empty(sops.Encrypted);
    }

    [Fact]
    public async Task Without_access_the_file_stays_encrypted_and_the_reason_is_told_once()
    {
        sops.Refusal = "you are not an Editor";
        var services = Services(null);
        var ciphertext = FakeSops.Wrap("TOKEN=x");

        var first = await new SmudgeOperation(services).RunAsync("a/.env", ciphertext, Ct);
        await new SmudgeOperation(services).RunAsync("b/.env", ciphertext, Ct);

        Assert.True(first.Success);
        Assert.Equal(ciphertext, first.Content);
        Assert.Equal(1, error.ToString().Split("you are not an Editor").Length - 1);
        Assert.Contains("b/.env stays encrypted.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dotenv_saved_with_crlf_is_encrypted_in_lf_and_counts_as_unchanged()
    {
        var services = Services(FilterCache.Open(gitDirectory));
        var ciphertext = FakeSops.Wrap("TOKEN=s3cret\nDEBUG=1\n");
        await new SmudgeOperation(services).RunAsync(".env", ciphertext, Ct);

        var unchanged = await new CleanOperation(services).RunAsync(".env", "TOKEN=s3cret\r\nDEBUG=1\r\n"u8.ToArray(), Ct);
        var changed = await new CleanOperation(services).RunAsync(".env", "TOKEN=other\r\n"u8.ToArray(), Ct);

        Assert.Equal(ciphertext, unchanged.Content);
        Assert.Equal("ENC1:TOKEN=other\n\nsops_version=3.13.3\n", Encoding.UTF8.GetString(changed.Content));
    }

    [Theory]
    [InlineData("app/.env", "A=1\nB=2\n")]
    [InlineData("config.INI", "[s]\nA=1\n")]
    [InlineData("secrets.yaml", "a: b\r\n")]
    [InlineData("secrets/cert.p12", "\r\n\u0001")]
    public void Only_dotenv_and_ini_lose_cr_before_encryption(string path, string expected)
    {
        var input = expected.Contains('\r', StringComparison.Ordinal) ? expected : expected.Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.Equal(expected, Encoding.UTF8.GetString(SopsFiles.PlainForSops(path, Encoding.UTF8.GetBytes(input))));
    }

    [Fact]
    public async Task Ciphertext_with_crlf_that_fails_to_decrypt_says_what_to_do()
    {
        sops.Refusal = "sops failed with exit code 1: parsing time";
        var crlf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(FakeSops.Wrap("TOKEN=x")).Replace("\n", "\r\n", StringComparison.Ordinal));

        var result = await new SmudgeOperation(Services(null)).RunAsync(".env", crlf, Ct);

        Assert.Equal(crlf, result.Content);
        Assert.Contains("CRLF line endings", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("forest secrets fix-eol", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_text_is_never_passed_on_when_encryption_fails()
    {
        sops.Refusal = "aggregator unreachable";

        var result = await new CleanOperation(Services(null)).RunAsync(".env", "TOKEN=x"u8.ToArray(), Ct);

        Assert.False(result.Success);
        Assert.Empty(result.Content);
    }

    [Fact]
    public async Task Decryptors_are_tried_in_order()
    {
        var local = new FakeSops { Refusal = "no local key" };
        var services = new FilterServices([local, sops], sops, null, error);

        var result = await new SmudgeOperation(services).RunAsync(".env", FakeSops.Wrap("A=1"), Ct);

        Assert.Equal("A=1", Encoding.UTF8.GetString(result.Content));
        Assert.Empty(error.ToString());
    }

    private FilterServices Services(FilterCache? cache) => new([sops], sops, cache, error);
}
