using System.Diagnostics;
using System.Text;
using ContainerForest.Aggregator.Application.Secrets;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Aggregator.Infrastructure.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Secrets;

/// <summary>
/// Runs the real sops and age-keygen. Without them the tests are skipped, unless CF_TEST_REQUIRE_SOPS=1 (as in CI),
/// where a missing tool is a failure.
/// </summary>
public sealed class SopsToolsTests
{
    private static readonly IOptions<SopsOptions> Options = Microsoft.Extensions.Options.Options.Create(new SopsOptions());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Parses_the_output_of_age_keygen()
    {
        var key = AgeKeygenCli.Parse("""
            # created: 2026-10-03T11:52:27+03:00
            # public key: age1dhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxng
            AGE-SECRET-KEY-1QQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQ

            """);

        Assert.Equal("age1dhw2uyj0rk688e62zmaz2xfaf2jn46ckkdey5ut7d8avperufqwq83nxng", key.Recipient);
        Assert.StartsWith("AGE-SECRET-KEY-1", key.Identity, StringComparison.Ordinal);
    }

    [Fact]
    public void Unexpected_output_is_an_error_that_does_not_quote_it()
    {
        var error = Assert.Throws<InvalidOperationException>(() => AgeKeygenCli.Parse("AGE-SECRET-KEY-1SECRET\n"));

        Assert.DoesNotContain("SECRET", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("secrets.yaml", "db:\n    password: hunter2\n")]
    [InlineData(".env", "API_KEY=abc123\n")]
    [InlineData("secrets/db_password", "raw-secret\n")]
    public async Task Decrypts_a_file_encrypted_for_one_of_the_keys(string path, string plainText)
    {
        RequireTools();
        var generator = new AgeKeygenCli(Options);
        var key = await generator.GenerateAsync(Ct);
        var other = await generator.GenerateAsync(Ct);
        var encrypted = await EncryptAsync(path, plainText, key.Recipient);

        var decrypted = await Decryptor().DecryptAsync(StackFilePath.Parse(path), encrypted, [other.Identity, key.Identity], Ct);

        Assert.Equal(plainText, Encoding.UTF8.GetString(decrypted));
    }

    [Fact]
    public async Task File_for_another_key_cannot_be_decrypted()
    {
        RequireTools();
        var generator = new AgeKeygenCli(Options);
        var key = await generator.GenerateAsync(Ct);
        var other = await generator.GenerateAsync(Ct);
        var encrypted = await EncryptAsync(".env", "API_KEY=abc123\n", key.Recipient);

        var error = await Assert.ThrowsAsync<SopsDecryptionException>(() =>
            Decryptor().DecryptAsync(StackFilePath.Parse(".env"), encrypted, [other.Identity], Ct));

        Assert.StartsWith("none of this environment's age keys", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Damaged_file_is_reported()
    {
        RequireTools();
        var key = await new AgeKeygenCli(Options).GenerateAsync(Ct);
        var encrypted = Encoding.UTF8.GetString(await EncryptAsync(".env", "API_KEY=abc123\n", key.Recipient));
        var damaged = Encoding.UTF8.GetBytes(encrypted.Replace("sops_version=", "sops_versio=", StringComparison.Ordinal));

        await Assert.ThrowsAsync<SopsDecryptionException>(() =>
            Decryptor().DecryptAsync(StackFilePath.Parse(".env"), damaged, [key.Identity], Ct));
    }

    [Fact]
    public async Task File_with_crlf_line_endings_is_reported_as_such()
    {
        RequireTools();
        var key = await new AgeKeygenCli(Options).GenerateAsync(Ct);
        var encrypted = Encoding.UTF8.GetString(await EncryptAsync(".env", "API_KEY=abc123\n", key.Recipient));
        var converted = Encoding.UTF8.GetBytes(encrypted.Replace("\n", "\r\n", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<SopsDecryptionException>(() =>
            Decryptor().DecryptAsync(StackFilePath.Parse(".env"), converted, [key.Identity], Ct));

        Assert.StartsWith("the file has CRLF line endings", error.Message, StringComparison.Ordinal);
        Assert.Contains("eol=lf", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("secrets.yaml", "db:\n    password: hunter2\n")]
    [InlineData(".env", "API_KEY=abc123\n")]
    [InlineData("secrets/db_password", "raw-secret\n")]
    public async Task Encrypts_for_the_recipients_of_the_rule(string path, string plainText)
    {
        RequireTools();
        var generator = new AgeKeygenCli(Options);
        var key = await generator.GenerateAsync(Ct);
        var other = await generator.GenerateAsync(Ct);

        var encrypted = await Decryptor().EncryptAsync(
            StackFilePath.Parse(path), Encoding.UTF8.GetBytes(plainText), new SopsEncryptionRule([key.Recipient, other.Recipient]), Ct);

        Assert.True(SopsFile.IsEncrypted(StackFilePath.Parse(path), encrypted));
        Assert.DoesNotContain(plainText.Trim(), Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);
        Assert.Equal(new[] { key.Recipient, other.Recipient }.Order(), SopsFile.AgeRecipients(encrypted).Order());
        var decrypted = await Decryptor().DecryptAsync(StackFilePath.Parse(path), encrypted, [other.Identity], Ct);
        Assert.Equal(plainText, Encoding.UTF8.GetString(decrypted));
    }

    [Fact]
    public async Task Encryption_honours_which_keys_to_encrypt()
    {
        RequireTools();
        var key = await new AgeKeygenCli(Options).GenerateAsync(Ct);

        var encrypted = Encoding.UTF8.GetString(await Decryptor().EncryptAsync(
            StackFilePath.Parse("app.yaml"), "name: web\npassword: hunter2\n"u8.ToArray(), new SopsEncryptionRule([key.Recipient], EncryptedRegex: "^password$"), Ct));

        Assert.Contains("name: web", encrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", encrypted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_input_fails_without_quoting_it()
    {
        RequireTools();
        var key = await new AgeKeygenCli(Options).GenerateAsync(Ct);

        var error = await Assert.ThrowsAsync<SopsEncryptionException>(() => Decryptor().EncryptAsync(
            StackFilePath.Parse("app.json"), "{\"password\": hunter2"u8.ToArray(), new SopsEncryptionRule([key.Recipient]), Ct));

        Assert.DoesNotContain("hunter2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_sops_is_a_readable_error()
    {
        var decryptor = new SopsCli(
            Microsoft.Extensions.Options.Options.Create(new SopsOptions { SopsPath = "/nonexistent/sops" }), NullLogger<SopsCli>.Instance);

        var error = await Assert.ThrowsAsync<SopsDecryptionException>(() =>
            decryptor.DecryptAsync(StackFilePath.Parse(".env"), [], ["AGE-SECRET-KEY-1X"], Ct));

        Assert.Equal("sops is not installed on the aggregator.", error.Message);
    }

    private static SopsCli Decryptor() => new(Options, NullLogger<SopsCli>.Instance);

    // Encrypts the way a user would: sops encrypt with the recipient, the format taken from the file name.
    private static async Task<byte[]> EncryptAsync(string path, string plainText, string recipient)
    {
        var result = await CliProcess.RunAsync(
            "sops",
            ["encrypt", "--age", recipient, "--filename-override", path, "/dev/stdin"],
            new Dictionary<string, string>(),
            Encoding.UTF8.GetBytes(plainText),
            TimeSpan.FromSeconds(30),
            Ct);
        Assert.True(result.ExitCode == 0, result.Stderr);
        return result.Stdout;
    }

    private static void RequireTools()
    {
        if (OnPath("sops") && OnPath("age-keygen"))
        {
            return;
        }

        const string message = "sops and age-keygen are not on PATH.";
        if (Environment.GetEnvironmentVariable("CF_TEST_REQUIRE_SOPS") == "1")
        {
            Assert.Fail(message);
        }

        Assert.Skip(message);
    }

    private static bool OnPath(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(tool, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process!.WaitForExit();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
