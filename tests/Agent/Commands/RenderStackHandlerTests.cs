using System.Text.Json.Nodes;
using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Tests.Commands;

/// <summary>Rendering a bundle for the aggregator's compose policy (ADR-0012).</summary>
public sealed class RenderStackHandlerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"cf-render-test-{Guid.NewGuid():N}");
    private readonly FakeComposeRunner compose = new();
    private readonly RecordingProgress progress = new();
    private readonly AgentOptions options;

    public RenderStackHandlerTests()
    {
        options = new AgentOptions
        {
            DataDirectory = Path.Combine(root, "data"),
            StacksDirectory = Path.Combine(root, "data", "stacks"),
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
    public async Task Bundle_is_rendered_in_a_throwaway_directory_with_its_own_env_file()
    {
        compose.Captured = """{"name":"web","services":{"web":{"image":"nginx"}}}""";

        var result = await Handler().HandleAsync(Message(Command()), progress, Ct);

        Assert.Null(result.Error);
        var run = Assert.Single(compose.Runs);
        Assert.Equal(
            ["compose", "--ansi", "never", "--progress", "plain", "-p", "web", "-f", "compose.yaml",
             "--env-file", Path.Combine(run.WorkingDirectory, ".env"), "config", "--format", "json"],
            run.Arguments);
        Assert.StartsWith(Path.Combine(options.SecretsDirectory, ".render"), run.WorkingDirectory, StringComparison.Ordinal);
        Assert.Equal("TAG=1", compose.FilesDuringCapture[".env"]);
        Assert.Equal("s3cr3t", compose.FilesDuringCapture["secrets/db_password"]);
        Assert.False(Directory.Exists(run.WorkingDirectory), "the render directory must not outlive the command");
        Assert.Equal("nginx", JsonNode.Parse(result.RenderedStack.ModelJson)!["services"]!["web"]!["image"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(options.StacksRoot, "web")), "rendering must not touch the stack directory");
        Assert.Equal(Path.Combine(options.StacksRoot, "web"), result.RenderedStack.StackDirectory);
        Assert.Contains(options.SecretsDirectory, result.RenderedStack.AgentDirectories);
    }

    [Fact]
    public async Task Model_gets_the_stack_directorys_paths_and_loses_what_may_hold_secrets()
    {
        compose.Captured = """
            {"services":{"web":{
              "environment":{"DB_PASSWORD":"s3cr3t"},
              "command":["--password","s3cr3t"],
              "labels":{"traefik.enable":"true","token":"s3cr3t"},
              "volumes":[{"type":"bind","source":"$DIR/data","target":"/data"},{"type":"volume","source":"db","target":"/db"}],
              "x-note":"s3cr3t"}},
             "secrets":{"pw":{"file":"$DIR/secrets/db_password","content":"s3cr3t"}}}
            """;

        var result = await Handler().HandleAsync(Message(Command()), progress, Ct);

        var model = result.RenderedStack.ModelJson;
        Assert.DoesNotContain("s3cr3t", model, StringComparison.Ordinal);
        var stack = Path.Combine(options.StacksRoot, "web").Replace('\\', '/');
        var parsed = JsonNode.Parse(model)!;
        Assert.Equal($"{stack}/data", parsed["services"]!["web"]!["volumes"]![0]!["source"]!.GetValue<string>());
        Assert.Equal($"{stack}/secrets/db_password", parsed["secrets"]!["pw"]!["file"]!.GetValue<string>());
        Assert.Equal("", parsed["services"]!["web"]!["labels"]!["traefik.enable"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bundle_without_env_file_renders_with_an_empty_one()
    {
        var command = Command();
        command.Files.RemoveAt(command.Files.ToList().FindIndex(f => f.Path == ".env"));

        await Handler().HandleAsync(Message(command), progress, Ct);

        var arguments = Assert.Single(compose.Runs).Arguments.ToList();
        Assert.Equal(OperatingSystem.IsWindows() ? "NUL" : "/dev/null", arguments[arguments.IndexOf("--env-file") + 1]);
    }

    [Fact]
    public async Task Mount_sources_through_links_and_out_of_sight_are_reported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var data = Path.Combine(options.DataDirectory, "volumes", "web");
        Directory.CreateDirectory(data);
        File.CreateSymbolicLink(Path.Combine(data, "root"), "/");
        compose.Captured = """
            {"services":{"web":{"volumes":[
              {"type":"bind","source":"DATA/root/etc","target":"/etc"},
              {"type":"bind","source":"DATA/plain","target":"/plain"},
              {"type":"bind","source":"/srv/media","target":"/media"}]}}}
            """.Replace("DATA", data, StringComparison.Ordinal);

        var result = await Handler().HandleAsync(Message(Command()), progress, Ct);

        var paths = result.RenderedStack.InspectedPaths.OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
        Assert.Equal(2, paths.Count);
        Assert.Equal(("/srv/media", true), (paths[0].Path, paths[0].Unchecked));
        Assert.Equal(($"{data}/root/etc", $"{data}/root", false), (paths[1].Path, paths[1].Link, paths[1].Unchecked));
    }

    [Fact]
    public async Task Output_that_is_not_json_fails_the_command_and_still_removes_the_directory()
    {
        compose.Captured = "not json";

        var result = await Handler().HandleAsync(Message(Command()), progress, Ct);

        Assert.Equal(ErrorCode.CommandFailed, result.Error.Code);
        Assert.False(Directory.Exists(Assert.Single(compose.Runs).WorkingDirectory));
    }

    private RenderStackHandler Handler() =>
        new(compose, new HostPathInspector(Options.Create(options)), Options.Create(options), NullLogger<RenderStackHandler>.Instance);

    private static RenderStack Command() => new()
    {
        StackName = "web",
        Files =
        {
            new StackFile { Path = "compose.yaml", Content = ByteString.CopyFromUtf8("services: {}"), Mode = 0b110_100_100 },
            new StackFile { Path = ".env", Content = ByteString.CopyFromUtf8("TAG=1"), Mode = 0b110_100_100 },
            new StackFile { Path = "secrets/db_password", Content = ByteString.CopyFromUtf8("s3cr3t"), Mode = 0b110_100_100, Secret = true },
        },
    };

    private static ControllerMessage Message(RenderStack command) => new() { RequestId = "r1", RenderStack = command };
}
