using System.Diagnostics;
using System.Text.Json.Nodes;
using ContainerForest.Agent.Commands;
using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Tests.Compose;

/// <summary>
/// Renders with the real `docker compose config`, which needs no Docker daemon. Skipped where the compose plugin is
/// missing, and required in CI (CF_TEST_REQUIRE_DOCKER_COMPOSE=1), since the policy depends on what compose prints.
/// </summary>
public sealed class RealComposeRenderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"cf-real-render-{Guid.NewGuid():N}");
    private readonly AgentOptions options;

    public RealComposeRenderTests()
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
    public async Task Variables_merges_and_relative_paths_are_resolved_as_compose_would_run_them()
    {
        SkipWithoutCompose();
        var command = new RenderStack
        {
            StackName = "web",
            Files =
            {
                File("compose.yaml", """
                    x-base: &base
                      restart: unless-stopped
                    services:
                      web:
                        <<: *base
                        image: nginx:${TAG:-1.27}
                        privileged: ${PRIVILEGED}
                        env_file: [app.env]
                        volumes:
                          - ./data:/data
                          - /etc/localtime:/etc/localtime:ro
                    """),
                File(".env", "PRIVILEGED=true\nTAG=1.29\n"),
                File("app.env", "PASSWORD=s3cr3t\n", secret: true),
            },
        };
        var handler = new RenderStackHandler(
            new DockerComposeCli(Options.Create(options), NullLogger<DockerComposeCli>.Instance),
            new HostPathInspector(Options.Create(options)),
            Options.Create(options),
            NullLogger<RenderStackHandler>.Instance);

        var result = await handler.HandleAsync(new ControllerMessage { RequestId = "r1", RenderStack = command }, new RecordingProgress(), Ct);

        Assert.Null(result.Error);
        var model = result.RenderedStack.ModelJson;
        Assert.DoesNotContain("s3cr3t", model, StringComparison.Ordinal);
        var web = JsonNode.Parse(model)!["services"]!["web"]!;
        Assert.True(web["privileged"]!.GetValue<bool>());
        Assert.Equal("nginx:1.29", web["image"]!.GetValue<string>());
        Assert.Equal("unless-stopped", web["restart"]!.GetValue<string>());
        Assert.Equal(Path.Combine(options.StacksRoot, "web", "data"), web["volumes"]![0]!["source"]!.GetValue<string>());
    }

    private static StackFile File(string path, string content, bool secret = false) =>
        new() { Path = path, Content = ByteString.CopyFromUtf8(content), Mode = 0b110_100_100, Secret = secret };

    private static void SkipWithoutCompose()
    {
        var required = Environment.GetEnvironmentVariable("CF_TEST_REQUIRE_DOCKER_COMPOSE") == "1";
        var available = !OperatingSystem.IsWindows() && ComposeAvailable();
        if (required && !available)
        {
            Assert.Fail("CF_TEST_REQUIRE_DOCKER_COMPOSE is set, but 'docker compose' does not run.");
        }

        Assert.SkipUnless(available, "The docker compose plugin is not installed.");
    }

    private static bool ComposeAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", ["compose", "version"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process!.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
