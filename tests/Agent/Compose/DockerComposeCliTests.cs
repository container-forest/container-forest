using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Configuration;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Tests.Compose;

/// <summary>
/// Runs a real process: /bin/sh stands in for docker, so the "compose" argument is the name of a script
/// in the working directory, and the remaining arguments are the script's.
/// </summary>
public sealed class DockerComposeCliTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"cf-compose-test-{Guid.NewGuid():N}");
    private readonly AgentOptions options = new() { DockerExecutable = "/bin/sh" };
    private readonly RecordingProgress progress = new();

    public DockerComposeCliTests()
    {
        Directory.CreateDirectory(directory);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public async Task Output_of_both_streams_is_reported_line_by_line()
    {
        SkipOnWindows();
        Script("""
            echo "args: $*"
            echo "to stderr" >&2
            echo "value: $APP_SETTING"
            """);

        await Cli().RunAsync(Up(new() { ["APP_SETTING"] = "from bundle" }), progress, Ct);

        Assert.Contains("args: --ansi never --progress plain -p web -f compose.yaml up -d --remove-orphans --pull missing --wait", progress.Lines);
        Assert.Contains("to stderr", progress.Lines);
        Assert.Contains("value: from bundle", progress.Lines);
    }

    [Fact]
    public async Task Agent_settings_are_not_passed_to_compose()
    {
        SkipOnWindows();
        Environment.SetEnvironmentVariable("CF_AGENT__TESTSECRET", "enrollment-secret");
        try
        {
            Script("""echo "secret: [$CF_AGENT__TESTSECRET]" """);

            await Cli().RunAsync(Up([]), progress, Ct);

            Assert.Equal(["secret: []"], progress.Lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CF_AGENT__TESTSECRET", null);
        }
    }

    [Fact]
    public async Task Non_zero_exit_code_is_a_command_failure()
    {
        SkipOnWindows();
        Script("exit 3");

        var ex = await Assert.ThrowsAsync<ComposeFailedException>(() => Cli().RunAsync(Up([]), progress, Ct));

        Assert.Equal(3, ex.ExitCode);
        Assert.Equal(ErrorCode.CommandFailed, ex.Code);
        Assert.Equal("docker compose up exited with code 3; see the command output.", ex.Message);
    }

    [Fact]
    public async Task Missing_docker_cli_is_reported_as_unavailable()
    {
        options.DockerExecutable = Path.Combine(directory, "no-such-docker");

        var ex = await Assert.ThrowsAsync<ComposeUnavailableException>(() => Cli().RunAsync(Up([]), progress, Ct));

        Assert.Equal(ErrorCode.DockerUnavailable, ex.Code);
    }

    [Fact]
    public async Task Compose_running_past_the_timeout_is_killed()
    {
        SkipOnWindows();
        options.ComposeTimeout = TimeSpan.FromMilliseconds(300);
        Script("""
            echo started
            sleep 30
            """);

        var started = DateTimeOffset.UtcNow;
        var ex = await Assert.ThrowsAsync<ComposeTimeoutException>(() => Cli().RunAsync(Up([]), progress, Ct));

        Assert.Equal(ErrorCode.Timeout, ex.Code);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Cancellation_kills_compose()
    {
        SkipOnWindows();
        Script("sleep 30");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(300));

        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Cli().RunAsync(Up([]), progress, cancel.Token));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Very_long_lines_are_truncated()
    {
        SkipOnWindows();
        Script($"""printf '%{DockerComposeCli.MaxLineLength + 100}s\n' x""");

        await Cli().RunAsync(Up([]), progress, Ct);

        var line = Assert.Single(progress.Lines);
        Assert.Equal(DockerComposeCli.MaxLineLength + " [truncated]".Length, line.Length);
    }

    private static void SkipOnWindows() =>
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/bin/sh stands in for the docker CLI.");

    private DockerComposeCli Cli() => new(Options.Create(options), NullLogger<DockerComposeCli>.Instance);

    private ComposeInvocation Up(Dictionary<string, string> environment) =>
        ComposeInvocation.Up("web", directory, PullPolicy.Missing, environment);

    private void Script(string body) => File.WriteAllText(Path.Combine(directory, "compose"), body + "\n");
}
