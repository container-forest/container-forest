using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Docker;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Tests.Stacks;

public sealed class UnmanagedStackFinderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"cf-unmanaged-test-{Guid.NewGuid():N}");
    private readonly FakeDockerGateway docker = new();
    private readonly AgentOptions options;

    public UnmanagedStackFinderTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "srv"));
        options = new AgentOptions
        {
            DataDirectory = Path.Combine(root, "data"),
            StacksDirectory = Path.Combine(root, "data", "stacks"),
            SecretsDirectory = Path.Combine(root, "secrets"),
            UnmanagedStackRoots = [Srv],
        };
    }

    private string Srv => Path.Combine(root, "srv");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private UnmanagedStackFinder Finder() => new(docker, Options.Create(options), NullLogger<UnmanagedStackFinder>.Instance);

    private string ComposeDir(string relative, string fileName = "compose.yaml", string content = "services: {}\n")
    {
        var directory = Path.Combine(Srv, relative);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
        return directory;
    }

    private void Container(string project, string workingDir, string[] configFiles, bool system = false, string[]? envFiles = null) =>
        docker.ComposeContainers.Add(new ComposeContainer(
            new ContainerInfo { Id = Guid.NewGuid().ToString("N"), Name = project + "-1", StackName = project, System = system },
            workingDir,
            configFiles,
            envFiles ?? []));

    [Fact]
    public async Task Without_roots_finds_nothing_and_does_not_ask_docker()
    {
        options.UnmanagedStackRoots = [];

        Assert.Empty(await Finder().FindAllAsync(Ct));
        Assert.Empty(docker.Calls);
    }

    [Fact]
    public async Task Finds_projects_by_labels_and_directories_by_compose_files()
    {
        var caddy = ComposeDir("caddy", "docker-compose.yml");
        Container("caddy", caddy, [Path.Combine(caddy, "docker-compose.yml")]);
        var runner = ComposeDir(Path.Combine("gitea", "runner"));

        var stacks = await Finder().FindAllAsync(Ct);

        Assert.Collection(
            stacks,
            s =>
            {
                Assert.Equal("caddy", s.Name);
                Assert.True(s.HasContainers);
                Assert.Equal(caddy, s.Directory);
            },
            s =>
            {
                Assert.Equal("runner", s.Name);
                Assert.False(s.HasContainers);
                Assert.Equal([Path.Combine(runner, "compose.yaml")], s.ConfigFiles);
            });
    }

    [Fact]
    public async Task A_labelled_project_claims_its_directory_whatever_the_scan_would_call_it()
    {
        var directory = ComposeDir("Web App");
        Container("custom", directory, [Path.Combine(directory, "compose.yaml")]);

        var stack = Assert.Single(await Finder().FindAllAsync(Ct));

        Assert.Equal("custom", stack.Name);
    }

    [Fact]
    public async Task Skips_projects_outside_the_roots_and_stacks_the_agent_deploys()
    {
        var elsewhere = Path.Combine(root, "elsewhere");
        Container("other", elsewhere, [Path.Combine(elsewhere, "compose.yaml")]);
        var deployed = Path.Combine(options.StacksRoot, "web");
        Container("web", deployed, [Path.Combine(deployed, "compose.yaml")]);

        Assert.Empty(await Finder().FindAllAsync(Ct));
    }

    [Fact]
    public async Task Skips_a_project_whose_files_lie_outside_the_roots()
    {
        var directory = ComposeDir("app");
        Container("app", directory, [Path.Combine(directory, "compose.yaml"), Path.Combine(root, "shared.yaml")]);

        // The directory is found by the scan instead, with its own compose file only.
        var stack = Assert.Single(await Finder().FindAllAsync(Ct));
        Assert.False(stack.HasContainers);
    }

    [Fact]
    public async Task Marks_a_project_with_a_system_container()
    {
        var directory = ComposeDir("forest");
        Container("forest", directory, [Path.Combine(directory, "compose.yaml")], system: true);

        Assert.True(Assert.Single(await Finder().FindAllAsync(Ct)).IsSystem);
    }

    [Fact]
    public async Task Does_not_look_below_a_directory_with_a_compose_file_or_into_hidden_ones()
    {
        ComposeDir("app");
        ComposeDir(Path.Combine("app", "nested"));
        ComposeDir(".hidden");

        Assert.Equal(["app"], (await Finder().FindAllAsync(Ct)).Select(s => s.Name));
    }

    [Fact]
    public async Task Does_not_follow_symbolic_links()
    {
        var target = Path.Combine(root, "outside");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "compose.yaml"), "services: {}\n");
        Directory.CreateSymbolicLink(Path.Combine(Srv, "link"), target);

        Assert.Empty(await Finder().FindAllAsync(Ct));
    }

    [Theory]
    [InlineData("name: from-file\nservices: {}\n", null, "from-file")]
    [InlineData("name: \"quoted\" # comment\n", null, "quoted")]
    [InlineData("services:\n  web:\n    name: nested\n", null, "my_app")]
    [InlineData("name: ${NAME}\n", null, null)]
    [InlineData("name: from-file\n", "COMPOSE_PROJECT_NAME=from-env\n", "from-env")]
    public void Project_name_follows_compose(string compose, string? env, string? expected)
    {
        var directory = ComposeDir("My_App", content: compose);
        if (env is not null)
        {
            File.WriteAllText(Path.Combine(directory, ".env"), env);
        }

        Assert.Equal(expected, UnmanagedStackFinder.ProjectNameOf(directory, Path.Combine(directory, "compose.yaml")));
    }

    [Theory]
    [InlineData("/srv/docker", true)]
    [InlineData("/opt/container-forest", false)]
    [InlineData("/opt/container-forest/stacks/x", false)]
    [InlineData("/opt", false)]
    [InlineData("relative", false)]
    public void Roots_must_be_absolute_and_outside_the_agents_own_directories(string root, bool valid)
    {
        var agent = new AgentOptions
        {
            ControllerUrl = new Uri("https://forest.example.com"),
            DataDirectory = "/opt/container-forest",
            UnmanagedStackRoots = [root],
        };

        Assert.Equal(valid, agent.IsValid());
    }
}
