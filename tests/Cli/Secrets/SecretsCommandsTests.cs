using System.CommandLine;
using System.Diagnostics;
using ContainerForest.Cli.Api;
using ContainerForest.Cli.Commands;
using ContainerForest.Cli.Configuration;
using ContainerForest.Cli.Login;

namespace ContainerForest.Cli.Tests.Secrets;

/// <summary>The secrets commands that need no aggregator, against a real git repository.</summary>
public sealed class SecretsCommandsTests : IDisposable
{
    private readonly string repository = Directory.CreateTempSubdirectory("cf-secrets-").FullName;

    public SecretsCommandsTests()
    {
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "test");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(repository, recursive: true);

    [Fact]
    public async Task Check_fails_on_a_file_under_the_filter_committed_in_plain_text()
    {
        Write(".gitattributes", "*.env filter=forest diff=forest\n");
        Write("app/.env", "TOKEN=hunter2\n");
        Write("app/compose.yaml", "services: {}\n");
        Git("add", "-A");
        Git("commit", "-qm", "leak");

        var (code, output, error) = await RunAsync("secrets", "check");

        Assert.Equal(ExitCodes.Failed, code);
        Assert.Contains("app/.env is under filter=forest but not encrypted", error, StringComparison.Ordinal);
        Assert.DoesNotContain("compose.yaml", error, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_passes_encrypted_files_and_looks_only_at_staged_ones_with_staged()
    {
        Write(".gitattributes", "*.env filter=forest diff=forest\n");
        Write("app/.env", System.Text.Encoding.UTF8.GetString(FakeSops.Wrap("TOKEN=x")));
        Write("old/.env", "TOKEN=old\n");
        Git("add", ".gitattributes", "app/.env");

        var (staged, _, _) = await RunAsync("secrets", "check", "--staged");
        Git("add", "old/.env");
        var (all, _, _) = await RunAsync("secrets", "check");

        Assert.Equal(ExitCodes.Ok, staged);
        Assert.Equal(ExitCodes.Failed, all);
    }

    [Fact]
    public async Task Check_fails_on_ciphertext_with_crlf_and_notes_rules_without_eol()
    {
        Git("config", "core.autocrlf", "false");
        Write(".gitattributes", "*.env filter=forest diff=forest\n");
        Write("app/.env", System.Text.Encoding.UTF8.GetString(FakeSops.Wrap("TOKEN=x")).Replace("\n", "\r\n", StringComparison.Ordinal));
        Git("add", "-A");

        var (code, _, error) = await RunAsync("secrets", "check");

        Assert.Equal(ExitCodes.Failed, code);
        Assert.Contains("app/.env is encrypted in git with CRLF line endings", error, StringComparison.Ordinal);
        Assert.Contains("fix-eol", error, StringComparison.Ordinal);
        Assert.Contains("have no eol=lf", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fix_eol_keeps_secret_files_in_lf_in_attributes_git_and_the_clone()
    {
        Git("config", "core.autocrlf", "false");
        Write(".gitattributes", "# secrets\r\n*.env filter=forest diff=forest\r\n*.png binary\r\n/raw/** filter=forest -text\r\n");
        var crlf = System.Text.Encoding.UTF8.GetString(FakeSops.Wrap("TOKEN=x")).Replace("\n", "\r\n", StringComparison.Ordinal);
        Write("app/.env", crlf);
        Write("db/.env", crlf);
        Git("add", "-A");
        Git("commit", "-qm", "secrets with crlf");
        Write("db/.env", System.Text.Encoding.UTF8.GetString(FakeSops.Wrap("TOKEN=edited by hand")));

        var (code, _, error) = await RunAsync("secrets", "fix-eol");

        Assert.True(code == ExitCodes.Ok, error);
        Assert.Equal("# secrets\r\n*.env filter=forest diff=forest eol=lf\r\n*.png binary\r\n/raw/** filter=forest -text\r\n", Read(".gitattributes"));
        Assert.DoesNotContain("\r", GitOutput("show", ":app/.env"), StringComparison.Ordinal);
        Assert.DoesNotContain("\r", GitOutput("show", ":db/.env"), StringComparison.Ordinal);
        Assert.Equal(crlf.Replace("\r\n", "\n", StringComparison.Ordinal), Read("app/.env"));
        Assert.Contains("TOKEN=edited by hand", Read("db/.env"), StringComparison.Ordinal);
        Assert.Contains("db/.env is encrypted here and differs from git", error, StringComparison.Ordinal);
        Assert.Contains("M  app/.env", GitOutput("status", "--porcelain"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_a_file_writes_eol_lf_and_gives_it_to_the_old_lines()
    {
        Write(".gitattributes", ".env filter=forest diff=forest\n");
        Git("config", "filter.forest.process", "forest git filter-process");
        Write("app/secret.json", "{}");

        var (code, _, error) = await RunAsync("secrets", "add", "app/secret.json");

        Assert.True(code == ExitCodes.Ok, error);
        Assert.Equal(
            ".env filter=forest diff=forest eol=lf\n# Added by 'forest secrets add' (ADR-0010).\n/app/secret.json filter=forest diff=forest eol=lf\n",
            Read(".gitattributes"));
    }

    [Fact]
    public async Task Removing_drops_the_line_in_either_form()
    {
        Write(".gitattributes", "/a/.env filter=forest diff=forest eol=lf\n/b/.env filter=forest diff=forest\n");
        var encrypted = System.Text.Encoding.UTF8.GetString(FakeSops.Wrap("TOKEN=x"));
        Write("a/.env", encrypted);
        Write("b/.env", encrypted);
        Git("add", "-A");
        Git("commit", "-qm", "secrets");

        var (code, _, error) = await RunAsync("secrets", "remove", "a/.env", "b/.env");

        Assert.True(code == ExitCodes.Ok, error);
        Assert.Equal("", Read(".gitattributes"));
    }

    [Fact]
    public async Task Adding_a_file_needs_the_filter_set_up()
    {
        Write("app/.env", "TOKEN=x\n");

        var (code, _, error) = await RunAsync("secrets", "add", "app/.env");

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("forest git setup", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repository, ".gitattributes")));
    }

    [Fact]
    public async Task Removed_file_stays_a_sops_file_in_git_and_its_own_line_goes()
    {
        Write(".gitattributes", "/app/.env filter=forest diff=forest\n/db/.env filter=forest diff=forest\n");
        var encrypted = System.Text.Encoding.UTF8.GetString(FakeSops.Wrap("TOKEN=x"));
        Write("app/.env", encrypted);
        Write("db/.env", encrypted);
        Git("add", "-A");
        Git("commit", "-qm", "secrets");

        var (code, _, error) = await RunAsync("secrets", "remove", "app/.env");

        Assert.True(code == ExitCodes.Ok, error);
        Assert.Equal("/db/.env filter=forest diff=forest\n", Read(".gitattributes"));
        Assert.Equal(encrypted, Read("app/.env"));
        Assert.Equal(" M .gitattributes\n", GitOutput("status", "--porcelain"));
    }

    [Fact]
    public async Task File_a_pattern_covers_gets_a_line_that_unsets_the_filter()
    {
        Write(".gitattributes", "*.env filter=forest diff=forest\n");
        Write("app/.env", System.Text.Encoding.UTF8.GetString(FakeSops.Wrap("TOKEN=x")));
        Git("add", "-A");
        Git("commit", "-qm", "secrets");

        var (code, _, error) = await RunAsync("secrets", "remove", "app/.env");

        Assert.True(code == ExitCodes.Ok, error);
        Assert.EndsWith("/app/.env -filter -diff\n", Read(".gitattributes"), StringComparison.Ordinal);
        Assert.Contains("filter: unset", GitOutput("check-attr", "filter", "--", "app/.env"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_keeps_ciphertext_only_when_git_has_it()
    {
        Write(".gitattributes", "*.env filter=forest diff=forest\n");
        Write("leaked/.env", "TOKEN=x\n");
        Git("add", "-A");
        Git("commit", "-qm", "leak");
        Write("new/.env", "TOKEN=y\n");

        var (leaked, _, leakedError) = await RunAsync("secrets", "remove", "leaked/.env");
        var (untracked, _, untrackedError) = await RunAsync("secrets", "remove", "new/.env");
        var (notFiltered, _, _) = await RunAsync("secrets", "remove", "README.md");

        Assert.Equal(ExitCodes.Usage, leaked);
        Assert.Contains("--plain", leakedError, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, untracked);
        Assert.Contains("Not in git yet", untrackedError, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, notFiltered);
        Assert.Equal("*.env filter=forest diff=forest\n", Read(".gitattributes"));
    }

    [Fact]
    public async Task Plain_needs_yes_and_stages_the_decrypted_text()
    {
        Write(".gitattributes", "*.env filter=forest diff=forest\n");
        Git("add", "-A");
        Git("commit", "-qm", "attributes");
        Write("app/.env", "LOG_LEVEL=debug\n");

        var (unconfirmed, _, _) = await RunAsync("secrets", "remove", "app/.env", "--plain");
        var (code, _, error) = await RunAsync("secrets", "remove", "app/.env", "--plain", "--yes");

        Assert.Equal(ExitCodes.Usage, unconfirmed);
        Assert.True(code == ExitCodes.Ok, error);
        Assert.Equal("LOG_LEVEL=debug\n", GitOutput("show", ":app/.env"));
    }

    [Fact]
    public async Task Plain_refuses_a_file_that_is_not_decrypted_here()
    {
        Write(".gitattributes", "*.env filter=forest diff=forest\n");
        Write("app/.env", System.Text.Encoding.UTF8.GetString(FakeSops.Wrap("TOKEN=x")));
        Git("add", "-A");
        Git("commit", "-qm", "secrets");

        var (code, _, error) = await RunAsync("secrets", "remove", "app/.env", "--plain", "--yes");

        Assert.Equal(ExitCodes.Failed, code);
        Assert.Contains("Not decrypted in this clone", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Teardown_removes_what_setup_wrote_and_encrypts_the_files_again()
    {
        Write(".gitattributes", "*.env filter=forest diff=forest\n");
        var encrypted = System.Text.Encoding.UTF8.GetString(FakeSops.Wrap("TOKEN=x"));
        Write("app/.env", encrypted);
        Git("add", "-A");
        Git("commit", "-qm", "secrets");
        Write("app/.env", "TOKEN=x\n"); // as smudge left it
        Git("config", "forest.context", "prod");
        Git("config", "diff.forest.textconv", "forest git textconv");
        Write(".git/hooks/pre-commit", $"#!/bin/sh\n{Cli.Secrets.GitFilterSetup.HookMarker}\nexec forest secrets check --staged\n");
        Write(".git/forest/key", "k");

        var (code, _, error) = await RunAsync("git", "teardown");

        Assert.True(code == ExitCodes.Ok, error);
        Assert.Equal(encrypted, Read("app/.env"));
        Assert.DoesNotContain("forest", GitOutput("config", "--local", "--list"), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repository, ".git", "hooks", "pre-commit")));
        Assert.False(Directory.Exists(Path.Combine(repository, ".git", "forest")));
        Assert.Equal("*.env filter=forest diff=forest\n", Read(".gitattributes"));
    }

    [Fact]
    public async Task Teardown_leaves_a_hook_it_did_not_write()
    {
        Write(".git/hooks/pre-commit", "#!/bin/sh\nmake lint && forest secrets check --staged\n");

        var (code, _, error) = await RunAsync("git", "teardown");

        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(File.Exists(Path.Combine(repository, ".git", "hooks", "pre-commit")));
        Assert.Contains("remove its 'secrets check' line yourself", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outside_a_repository_the_commands_say_so()
    {
        var elsewhere = Directory.CreateTempSubdirectory("cf-not-git-").FullName;
        try
        {
            var (code, _, error) = await RunInAsync(elsewhere, "secrets", "check");

            Assert.NotEqual(ExitCodes.Ok, code);
            Assert.Contains("git", error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    private Task<(int Code, string Output, string Error)> RunAsync(params string[] args) => RunInAsync(repository, args);

    private static async Task<(int Code, string Output, string Error)> RunInAsync(string directory, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var host = new CliHost(
            output,
            error,
            TextReader.Null,
            _ => null,
            new ConfigStore(Path.Combine(directory, ".forest-config")),
            new ForestApiFactory(),
            new NoBrowser(),
            TimeProvider.System,
            workingDirectory: directory);
        var code = await ForestCli.Build(host).Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Ct);
        return (code, output.ToString(), error.ToString());
    }

    private void Write(string path, string content)
    {
        var full = Path.Combine(repository, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Read(string path) => File.ReadAllText(Path.Combine(repository, path));

    private string GitOutput(params string[] args)
    {
        var startInfo = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return output;
    }

    private void Git(params string[] args)
    {
        var startInfo = new ProcessStartInfo("git") { WorkingDirectory = repository, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private sealed class NoBrowser : IBrowser
    {
        public bool TryOpen(Uri page) => false;
    }
}
