using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Tests.Commands;

public sealed class StackCommandHandlerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"cf-stack-handler-test-{Guid.NewGuid():N}");
    private readonly FakeStackFileStore store = new();
    private readonly FakeComposeRunner compose = new();
    private readonly StackLocks locks = new();
    private readonly RecordingProgress progress = new();
    private readonly AgentOptions options;

    public StackCommandHandlerTests()
    {
        options = new AgentOptions
        {
            DataDirectory = Path.Combine(root, "data"),
            StacksDirectory = Path.Combine(root, "stacks"),
            SecretsDirectory = Path.Combine(root, "secrets"),
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Handlers_derive_the_handled_command_from_their_type()
    {
        Assert.Equal(ControllerMessage.BodyOneofCase.DeployStack, Deploy().Handles);
        Assert.Equal(ControllerMessage.BodyOneofCase.RestartStack, Restart().Handles);
        Assert.Equal(ControllerMessage.BodyOneofCase.DownStack, Down().Handles);
    }

    [Theory]
    [InlineData(PullPolicy.Missing, "missing")]
    [InlineData(PullPolicy.Always, "always")]
    public async Task Deploy_writes_the_files_and_runs_compose_up_in_the_stack_directory(PullPolicy pull, string pullArgument)
    {
        var result = await Deploy().HandleAsync(Message(DeployCommand(pull)), progress, Ct);

        Assert.Null(result.Error);
        Assert.Equal("r1", result.RequestId);
        Assert.Equal("web", Assert.Single(store.Written).StackName);
        var run = Assert.Single(compose.Runs);
        Assert.Equal(
            ["compose", "--ansi", "never", "--progress", "plain", "-p", "web", "-f", "compose.yaml",
             "up", "-d", "--remove-orphans", "--pull", pullArgument, "--wait"],
            run.Arguments);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "stacks", "web"), run.WorkingDirectory);
    }

    [Fact]
    public async Task Deploy_with_an_explicit_env_file_reads_only_the_bundles_env()
    {
        var command = DeployCommand();
        command.ExplicitEnvFile = true;

        await Deploy().HandleAsync(Message(command), progress, Ct);
        command.Files.Add(new StackFile { Path = ".env", Content = ByteString.CopyFromUtf8("TAG=1"), Mode = 0b110_100_100 });
        await Deploy().HandleAsync(Message(command), progress, Ct);

        var directory = Path.Combine(Path.GetTempPath(), "stacks", "web");
        Assert.Equal(OperatingSystem.IsWindows() ? "NUL" : "/dev/null", EnvFileOf(compose.Runs[0]));
        Assert.Equal(Path.Combine(directory, ".env"), EnvFileOf(compose.Runs[1]));
    }

    [Fact]
    public async Task Deploy_without_an_explicit_env_file_leaves_env_lookup_to_compose()
    {
        await Deploy().HandleAsync(Message(DeployCommand()), progress, Ct);

        Assert.DoesNotContain("--env-file", Assert.Single(compose.Runs).Arguments);
    }

    [Fact]
    public async Task Deploy_stops_before_compose_up_when_a_mount_source_goes_through_a_link()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var data = Path.Combine(options.DataDirectory, "data", "web");
        Directory.CreateDirectory(data);
        File.CreateSymbolicLink(Path.Combine(data, "root"), "/");
        var command = DeployCommand();
        command.VerifyPaths.Add(Path.Combine(data, "root", "etc"));

        var result = await Deploy().HandleAsync(Message(command), progress, Ct);

        Assert.Equal(ErrorCode.CommandFailed, result.Error.Code);
        Assert.Contains("symbolic link", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(compose.Runs);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/opt/../etc")]
    [InlineData("/opt//x")]
    public async Task Deploy_refuses_paths_to_verify_that_are_not_normalized(string path)
    {
        var command = DeployCommand();
        command.VerifyPaths.Add(path);

        var result = await Deploy().HandleAsync(Message(command), progress, Ct);

        Assert.Equal(ErrorCode.InvalidArgument, result.Error.Code);
        Assert.Empty(store.Written);
    }

    private static string EnvFileOf(ComposeInvocation run)
    {
        var index = run.Arguments.ToList().IndexOf("--env-file");
        Assert.True(index >= 0, "compose got no --env-file");
        return run.Arguments[index + 1];
    }

    [Fact]
    public async Task Deploy_discards_the_previous_secrets_only_after_compose_succeeded()
    {
        await Deploy().HandleAsync(Message(DeployCommand()), progress, Ct);
        Assert.Equal(["write web", "discard-previous-secrets web"], store.Calls);

        store.Calls.Clear();
        compose.Failure = new ComposeFailedException("docker compose up", 1);
        await Deploy().HandleAsync(Message(DeployCommand()), progress, Ct);
        Assert.Equal(["write web"], store.Calls);
    }

    [Fact]
    public async Task Deploy_restarts_the_stack_after_compose_up_when_its_other_files_changed()
    {
        store.FilesChanged = true;
        var command = DeployCommand();
        command.RestartOnFileChange = true;

        var result = await Deploy().HandleAsync(Message(command), progress, Ct);

        Assert.Null(result.Error);
        Assert.Equal(2, compose.Runs.Count);
        Assert.Contains("up", compose.Runs[0].Arguments);
        Assert.Equal(
            ["compose", "--ansi", "never", "--progress", "plain", "-p", "web", "restart", "--timeout", "10"],
            compose.Runs[1].Arguments);
        Assert.Equal(options.StacksRoot, compose.Runs[1].WorkingDirectory);
        Assert.Contains(progress.Lines, line => line.Contains("restarting the stack", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Deploy_does_not_restart_without_the_option_or_without_changed_files(bool restartOnFileChange, bool filesChanged)
    {
        store.FilesChanged = filesChanged;
        var command = DeployCommand();
        command.RestartOnFileChange = restartOnFileChange;

        await Deploy().HandleAsync(Message(command), progress, Ct);

        Assert.Contains("up", Assert.Single(compose.Runs).Arguments);
    }

    [Fact]
    public async Task Deploy_without_a_pull_policy_is_rejected_before_anything_is_written()
    {
        var result = await Deploy().HandleAsync(Message(DeployCommand(PullPolicy.Unspecified)), progress, Ct);

        Assert.Equal(ErrorCode.InvalidArgument, result.Error.Code);
        Assert.Empty(store.Written);
        Assert.Empty(compose.Runs);
    }

    [Fact]
    public async Task Deploy_with_an_invalid_path_writes_nothing()
    {
        var command = DeployCommand();
        command.Files.Add(new StackFile { Path = "../other/compose.yaml", Content = ByteString.Empty, Mode = 0b110_100_100 });

        var result = await Deploy().HandleAsync(Message(command), progress, Ct);

        Assert.Equal(ErrorCode.InvalidArgument, result.Error.Code);
        Assert.Empty(store.Written);
    }

    [Theory]
    [InlineData("Web")]
    [InlineData("web\n")]
    [InlineData("../web")]
    public async Task Invalid_stack_names_never_reach_the_disk_or_compose(string stackName)
    {
        var command = DeployCommand();
        command.StackName = stackName;

        var deploy = await Deploy().HandleAsync(Message(command), progress, Ct);
        var down = await Down().HandleAsync(Message(new DownStack { StackName = stackName }), progress, Ct);

        Assert.Equal(ErrorCode.InvalidArgument, deploy.Error.Code);
        Assert.Equal(ErrorCode.InvalidArgument, down.Error.Code);
        Assert.Empty(store.Calls);
        Assert.Empty(compose.Runs);
    }

    [Fact]
    public async Task Compose_output_is_reported_as_progress()
    {
        compose.Output.AddRange([" Container web-app-1  Started", " Container web-app-1  Healthy"]);

        await Deploy().HandleAsync(Message(DeployCommand()), progress, Ct);

        Assert.Equal(
            ["Writing 2 file(s) at 0123456789ab, 1 of them secret.", " Container web-app-1  Started", " Container web-app-1  Healthy"],
            progress.Lines);
    }

    [Fact]
    public async Task Secret_content_and_env_values_never_appear_in_progress_or_errors()
    {
        var command = DeployCommand();
        command.Env.Add("DB_PASSWORD", "hunter2");
        compose.Failure = new ComposeFailedException("docker compose up", 17);

        var result = await Deploy().HandleAsync(Message(command), progress, Ct);

        Assert.Equal(ErrorCode.CommandFailed, result.Error.Code);
        Assert.Equal("docker compose up exited with code 17; see the command output.", result.Error.Message);
        Assert.All(progress.Lines.Append(result.Error.Message), line =>
        {
            Assert.DoesNotContain("s3cr3t", line);
            Assert.DoesNotContain("hunter2", line);
        });
    }

    [Fact]
    public async Task Env_is_passed_to_compose_as_process_environment()
    {
        var command = DeployCommand();
        command.Env.Add("DB_PASSWORD", "hunter2");

        await Deploy().HandleAsync(Message(command), progress, Ct);

        Assert.Equal("hunter2", Assert.Single(compose.Runs).Environment["DB_PASSWORD"]);
    }

    [Theory]
    [InlineData("1BAD")]
    [InlineData("A-B")]
    [InlineData("A=B")]
    [InlineData("")]
    public async Task Invalid_env_names_are_rejected(string name)
    {
        var command = DeployCommand();
        command.Env.Add(name, "value");

        var result = await Deploy().HandleAsync(Message(command), progress, Ct);

        Assert.Equal(ErrorCode.InvalidArgument, result.Error.Code);
        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task Registry_credentials_are_given_to_compose_in_a_temporary_docker_config()
    {
        var command = DeployCommand();
        command.RegistryAuths.Add(new RegistryAuth { Server = "ghcr.io", Username = "bot", Password = "token" });

        var result = await Deploy().HandleAsync(Message(command), progress, Ct);

        Assert.Null(result.Error);
        var dockerConfig = Assert.Single(compose.Runs).Environment[RegistryConfigWriter.DockerConfigVariable];
        Assert.True(compose.DockerConfigExistedDuringRun);
        Assert.False(Directory.Exists(dockerConfig));
    }

    [Fact]
    public async Task Without_registry_credentials_compose_uses_its_own_docker_config()
    {
        await Deploy().HandleAsync(Message(DeployCommand()), progress, Ct);

        Assert.False(Assert.Single(compose.Runs).Environment.ContainsKey(RegistryConfigWriter.DockerConfigVariable));
    }

    [Fact]
    public async Task Invalid_registry_credentials_are_rejected_before_anything_is_written()
    {
        var command = DeployCommand();
        command.RegistryAuths.Add(new RegistryAuth { Server = "ghcr.io", Username = "a:b", Password = "token" });

        var result = await Deploy().HandleAsync(Message(command), progress, Ct);

        Assert.Equal(ErrorCode.InvalidArgument, result.Error.Code);
        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task Second_operation_on_the_same_stack_is_busy_while_the_first_runs()
    {
        store.Deployed.Add("web");
        compose.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Deploy().HandleAsync(Message(DeployCommand()), progress, Ct);
        while (!locks.IsHeld("web"))
        {
            await Task.Delay(10, Ct);
        }

        var second = await Restart().HandleAsync(Message(new RestartStack { StackName = "web", TimeoutSeconds = 10 }), progress, Ct);
        compose.Gate.SetResult();
        var firstResult = await first;

        Assert.Equal(ErrorCode.Busy, second.Error.Code);
        Assert.Null(firstResult.Error);
        Assert.False(locks.IsHeld("web"));
    }

    [Fact]
    public async Task Operations_on_different_stacks_run_at_the_same_time()
    {
        store.Deployed.Add("api");
        compose.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Deploy().HandleAsync(Message(DeployCommand()), progress, Ct);
        while (!locks.IsHeld("web"))
        {
            await Task.Delay(10, Ct);
        }

        var second = Restart().HandleAsync(Message(new RestartStack { StackName = "api", TimeoutSeconds = 10 }), progress, Ct);
        while (compose.Runs.Count < 2)
        {
            await Task.Delay(10, Ct);
        }

        compose.Gate.SetResult();

        Assert.Null((await first).Error);
        Assert.Null((await second).Error);
    }

    [Fact]
    public async Task Failed_operation_releases_the_stack()
    {
        compose.Failure = new ComposeFailedException("docker compose up", 1);
        await Deploy().HandleAsync(Message(DeployCommand()), progress, Ct);

        Assert.False(locks.IsHeld("web"));
    }

    [Fact]
    public async Task Restart_and_down_run_compose_without_the_compose_file_and_with_the_timeout()
    {
        store.Deployed.Add("web");

        await Restart().HandleAsync(Message(new RestartStack { StackName = "web", TimeoutSeconds = 30 }), progress, Ct);
        await Down().HandleAsync(Message(new DownStack { StackName = "web", TimeoutSeconds = 0 }), progress, Ct);

        Assert.Equal(
            ["compose", "--ansi", "never", "--progress", "plain", "-p", "web", "restart", "--timeout", "30"],
            compose.Runs[0].Arguments);
        Assert.Equal(
            ["compose", "--ansi", "never", "--progress", "plain", "-p", "web", "down", "--timeout", "0"],
            compose.Runs[1].Arguments);
        Assert.All(compose.Runs, run =>
        {
            Assert.DoesNotContain("-f", run.Arguments);
            Assert.Equal(options.StacksRoot, run.WorkingDirectory);
        });
    }

    [Fact]
    public async Task Down_deletes_the_secrets_after_compose_succeeded()
    {
        store.Deployed.Add("web");

        await Down().HandleAsync(Message(new DownStack { StackName = "web", TimeoutSeconds = 10 }), progress, Ct);

        Assert.Equal(["delete-secrets web"], store.Calls);
    }

    [Fact]
    public async Task Failed_down_keeps_the_secrets()
    {
        store.Deployed.Add("web");
        compose.Failure = new ComposeFailedException("docker compose down", 1);

        var result = await Down().HandleAsync(Message(new DownStack { StackName = "web", TimeoutSeconds = 10 }), progress, Ct);

        Assert.Equal(ErrorCode.CommandFailed, result.Error.Code);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task Restart_and_down_of_a_stack_that_was_never_deployed_are_not_found()
    {
        var restart = await Restart().HandleAsync(Message(new RestartStack { StackName = "web", TimeoutSeconds = 10 }), progress, Ct);
        var down = await Down().HandleAsync(Message(new DownStack { StackName = "web", TimeoutSeconds = 10 }), progress, Ct);

        Assert.Equal(ErrorCode.NotFound, restart.Error.Code);
        Assert.Equal(ErrorCode.NotFound, down.Error.Code);
        Assert.Empty(compose.Runs);
    }

    [Fact]
    public async Task Negative_timeout_is_an_invalid_argument()
    {
        store.Deployed.Add("web");

        var result = await Down().HandleAsync(Message(new DownStack { StackName = "web", TimeoutSeconds = -1 }), progress, Ct);

        Assert.Equal(ErrorCode.InvalidArgument, result.Error.Code);
        Assert.Empty(compose.Runs);
    }

    private DeployStackHandler Deploy() => new(
        store,
        compose,
        new RegistryConfigWriter(Options.Create(options), NullLogger<RegistryConfigWriter>.Instance),
        new HostPathInspector(Options.Create(options)),
        locks,
        Options.Create(options),
        NullLogger<DeployStackHandler>.Instance);

    private RestartStackHandler Restart() =>
        new(store, compose, locks, Options.Create(options), NullLogger<RestartStackHandler>.Instance);

    private DownStackHandler Down() =>
        new(store, compose, locks, Options.Create(options), NullLogger<DownStackHandler>.Instance);

    private static DeployStack DeployCommand(PullPolicy pull = PullPolicy.Missing) => new()
    {
        StackName = "web",
        CommitSha = "0123456789abcdef0123456789abcdef01234567",
        Pull = pull,
        Files =
        {
            new StackFile { Path = "compose.yaml", Content = ByteString.CopyFromUtf8("services: {}"), Mode = 0b110_100_100 },
            new StackFile { Path = "secrets/db_password", Content = ByteString.CopyFromUtf8("s3cr3t"), Mode = 0b110_100_100, Secret = true },
        },
    };

    private static ControllerMessage Message(DeployStack command) => new() { RequestId = "r1", DeployStack = command };

    private static ControllerMessage Message(RestartStack command) => new() { RequestId = "r1", RestartStack = command };

    private static ControllerMessage Message(DownStack command) => new() { RequestId = "r1", DownStack = command };
}
