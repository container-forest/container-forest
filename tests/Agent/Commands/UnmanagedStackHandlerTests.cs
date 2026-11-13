using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Docker;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Tests.Commands;

public sealed class UnmanagedStackHandlerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"cf-unmanaged-handler-test-{Guid.NewGuid():N}");
    private readonly FakeDockerGateway docker = new();
    private readonly FakeComposeRunner compose = new();
    private readonly StackLocks locks = new();
    private readonly RecordingProgress progress = new();
    private readonly AgentOptions options;
    private readonly string caddy;

    public UnmanagedStackHandlerTests()
    {
        options = new AgentOptions
        {
            DataDirectory = Path.Combine(root, "data"),
            StacksDirectory = Path.Combine(root, "data", "stacks"),
            SecretsDirectory = Path.Combine(root, "secrets"),
            UnmanagedStackRoots = [Path.Combine(root, "srv")],
        };

        caddy = Path.Combine(root, "srv", "caddy");
        Directory.CreateDirectory(caddy);
        File.WriteAllText(Path.Combine(caddy, "docker-compose.yml"), "services: {}\n");
        docker.ComposeContainers.Add(new ComposeContainer(
            new ContainerInfo { Id = "c1", Name = "caddy", StackName = "caddy" },
            caddy,
            [Path.Combine(caddy, "docker-compose.yml")],
            [Path.Combine(caddy, "prod.env")]));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private UnmanagedStackFinder Finder() => new(docker, Options.Create(options), NullLogger<UnmanagedStackFinder>.Instance);

    private ControlUnmanagedStackHandler Control() => new(
        Finder(),
        compose,
        new RegistryConfigWriter(Options.Create(options), NullLogger<RegistryConfigWriter>.Instance),
        locks,
        Options.Create(options),
        NullLogger<ControlUnmanagedStackHandler>.Instance);

    private static ControllerMessage Message(string stack, UnmanagedStackOperation operation, int timeout = 10) => new()
    {
        RequestId = "r1",
        ControlUnmanagedStack = new ControlUnmanagedStack { StackName = stack, Operation = operation, TimeoutSeconds = timeout },
    };

    [Fact]
    public async Task List_reports_the_stacks_found()
    {
        var handler = new ListUnmanagedStacksHandler(Finder(), NullLogger<ListUnmanagedStacksHandler>.Instance);

        var result = await handler.HandleAsync(
            new ControllerMessage { RequestId = "r1", ListUnmanagedStacks = new ListUnmanagedStacks() }, progress, Ct);

        var stack = Assert.Single(result.UnmanagedStacks.Items);
        Assert.Equal("caddy", stack.Name);
        Assert.Equal(caddy, stack.Directory);
        Assert.True(stack.HasContainers);
    }

    [Fact]
    public async Task Up_runs_compose_with_the_files_the_project_was_started_with()
    {
        var result = await Control().HandleAsync(Message("caddy", UnmanagedStackOperation.Up), progress, Ct);

        Assert.Null(result.Error);
        var run = Assert.Single(compose.Runs);
        Assert.Equal(caddy, run.WorkingDirectory);
        Assert.Equal(
            [
                "compose", "--ansi", "never", "--progress", "plain", "-p", "caddy", "--project-directory", caddy,
                "-f", Path.Combine(caddy, "docker-compose.yml"), "--env-file", Path.Combine(caddy, "prod.env"), "up", "-d",
            ],
            run.Arguments);
    }

    [Fact]
    public async Task Pull_runs_compose_pull()
    {
        await Control().HandleAsync(Message("caddy", UnmanagedStackOperation.Pull), progress, Ct);

        Assert.Equal("pull", Assert.Single(compose.Runs).Arguments[^1]);
    }

    [Theory]
    [InlineData(UnmanagedStackOperation.Restart, "restart")]
    [InlineData(UnmanagedStackOperation.Down, "down")]
    public async Task Restart_and_down_run_without_compose_files_outside_the_project(UnmanagedStackOperation operation, string verb)
    {
        await Control().HandleAsync(Message("caddy", operation, timeout: 3), progress, Ct);

        var run = Assert.Single(compose.Runs);
        Assert.Equal(options.StacksRoot, run.WorkingDirectory);
        Assert.True(Directory.Exists(options.StacksRoot));
        Assert.Equal(["compose", "--ansi", "never", "--progress", "plain", "-p", "caddy", verb, "--timeout", "3"], run.Arguments);
    }

    [Fact]
    public async Task An_unknown_stack_is_not_found_and_nothing_runs()
    {
        var result = await Control().HandleAsync(Message("web", UnmanagedStackOperation.Down), progress, Ct);

        Assert.Equal(ErrorCode.NotFound, result.Error.Code);
        Assert.Empty(compose.Runs);
    }

    [Fact]
    public async Task An_unspecified_operation_is_rejected()
    {
        var result = await Control().HandleAsync(Message("caddy", UnmanagedStackOperation.Unspecified), progress, Ct);

        Assert.Equal(ErrorCode.InvalidArgument, result.Error.Code);
    }

    [Fact]
    public async Task A_busy_stack_is_refused()
    {
        using var held = locks.TryAcquire("caddy");

        var result = await Control().HandleAsync(Message("caddy", UnmanagedStackOperation.Up), progress, Ct);

        Assert.Equal(ErrorCode.Busy, result.Error.Code);
    }

    [Fact]
    public async Task Remove_container_calls_docker()
    {
        var handler = new RemoveContainerHandler(docker, NullLogger<RemoveContainerHandler>.Instance);

        var result = await handler.HandleAsync(
            new ControllerMessage { RequestId = "r1", RemoveContainer = new RemoveContainer { ContainerId = "c1" } }, progress, Ct);

        Assert.Null(result.Error);
        Assert.Contains("remove c1", docker.Calls);
    }
}
