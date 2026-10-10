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
    public async Task Adding_a_file_needs_the_filter_set_up()
    {
        Write("app/.env", "TOKEN=x\n");

        var (code, _, error) = await RunAsync("secrets", "add", "app/.env");

        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("forest git setup", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repository, ".gitattributes")));
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
