using System.CommandLine;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Cli.Api;
using ContainerForest.Cli.Commands;
using ContainerForest.Cli.Configuration;
using ContainerForest.Cli.Login;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>The forest CLI (ADR-0008) against the aggregator in memory, with a fake agent behind it.</summary>
public sealed class CliEndToEndTests : IDisposable
{
    private readonly string configDirectory = Path.Combine(Path.GetTempPath(), $"forest-cli-{Guid.NewGuid():N}");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(configDirectory))
        {
            Directory.Delete(configDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Login_through_the_browser_saves_a_token_the_next_command_uses()
    {
        await using var app = new AggregatorApp();
        var browser = new ApprovingBrowser(app);

        var login = await RunAsync(app, browser, "", "login", "http://localhost");
        var whoami = await RunAsync(app, browser, "", "whoami", "-o", "json");

        Assert.Equal(0, login.ExitCode);
        Assert.Contains("Logged in to http://localhost/ as admin", login.Error);
        Assert.Equal(0, whoami.ExitCode);
        Assert.Equal(AggregatorApp.AdminUserName, JsonDocument.Parse(whoami.Output).RootElement.GetProperty("userName").GetString());
        Assert.Contains("\"loggedIn\":true", (await RunAsync(app, browser, "", "context", "list", "-o", "json")).Output);
    }

    [Fact]
    public async Task Logout_revokes_the_token_on_the_server()
    {
        await using var app = new AggregatorApp();
        var token = await CreateTokenAsync(app);
        await RunAsync(app, null, token, "login", "http://localhost", "--with-token");

        var logout = await RunAsync(app, null, "", "logout");
        var afterwards = await RunAsync(app, null, "", "--url", "http://localhost", "--token", token, "whoami");

        Assert.Equal(0, logout.ExitCode);
        Assert.Equal(3, afterwards.ExitCode);
    }

    [Fact]
    public async Task Containers_of_the_only_environment_are_listed()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        var token = await CreateTokenAsync(app);

        var listing = RunAsync(app, null, "", "--url", "http://localhost", "--token", token, "container", "list");
        var request = await agent.ReceiveAsync();
        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = request.RequestId,
                Containers = new ContainerList
                {
                    Items = { new ContainerInfo { Id = "c1", Name = "web-app-1", State = global::ContainerForest.Contracts.Agent.ContainerState.Running, Image = "nginx:1.27" } },
                },
            },
        });
        var result = await listing;

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("web-app-1", result.Output);
        Assert.Contains("running", result.Output);
    }

    [Fact]
    public async Task Deploy_follows_the_deployment_and_prints_its_log()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        await CreateStackAsync(app, agent);
        var token = await CreateTokenAsync(app);

        var deploying = RunAsync(app, null, "", "--url", "http://localhost", "--token", token, "stack", "deploy", "web", "--pull");
        var command = await agent.ReceiveAsync();
        Assert.Equal(PullPolicy.Always, command.DeployStack.Pull);
        await agent.SendAsync(new AgentMessage { Progress = new CommandProgress { RequestId = command.RequestId, Line = "Container web-web-1 Started" } });
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });
        var result = await deploying;

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Container web-web-1 Started", result.Output);
        Assert.Contains("Deployed 0123456789ab.", result.Error);
    }

    [Fact]
    public async Task Read_only_token_cannot_deploy()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        await CreateStackAsync(app, agent);
        var token = await CreateTokenAsync(app, readOnly: true);

        var result = await RunAsync(app, null, "", "--url", "http://localhost", "--token", token, "stack", "deploy", "web");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("read-only", result.Error);
    }

    [Fact]
    public async Task Destructive_command_needs_confirmation()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        await CreateStackAsync(app, agent);
        var token = await CreateTokenAsync(app);

        var result = await RunAsync(app, null, "", "--url", "http://localhost", "--token", token, "stack", "down", "web");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--yes", result.Error);
    }

    [Fact]
    public async Task Administrator_creates_a_user_with_a_role_from_the_command_line()
    {
        await using var app = new AggregatorApp();
        var environment = await app.CreateEnvironmentAsync("r2");
        var token = await CreateTokenAsync(app);
        string[] connection = ["--url", "http://localhost", "--token", token];

        var created = await RunAsync(app, null, "a long enough password\n", [.. connection, "user", "create", "alice"]);
        var granted = await RunAsync(app, null, "", [.. connection, "user", "role", "grant", "alice", "-e", "r2", "--role", "operator"]);
        var shown = await RunAsync(app, null, "", [.. connection, "user", "show", "alice", "-o", "json"]);

        Assert.Equal(0, created.ExitCode);
        Assert.Equal(0, granted.ExitCode);
        var role = Assert.Single(JsonDocument.Parse(shown.Output).RootElement.GetProperty("roleAssignments").EnumerateArray());
        Assert.Equal("Operator", role.GetProperty("role").GetString());
        Assert.Equal(environment.Id, role.GetProperty("environmentId").GetGuid());
    }

    [Fact]
    public async Task Repository_and_notification_rule_are_added_from_the_command_line()
    {
        await using var app = new AggregatorApp();
        await app.CreateEnvironmentAsync("r2");
        var token = await CreateTokenAsync(app);
        string[] connection = ["--url", "http://localhost", "--token", token];

        var repository = await RunAsync(app, null, "", [.. connection, "repo", "add", "app", "--url", "https://git.example.com/app.git"]);
        var repositories = await RunAsync(app, null, "", [.. connection, "repo", "list"]);
        await RunAsync(app, null, "", [.. connection, "notification", "channel", "add", "ops", "--discord-channel", "123456789012345678"]);
        var rule = await RunAsync(app, null, "", [.. connection, "notification", "rule", "add", "ops", "--event", "all", "-e", "r2"]);
        var rules = await RunAsync(app, null, "", [.. connection, "notification", "rule", "list", "ops", "-o", "json"]);

        Assert.Equal(0, repository.ExitCode);
        Assert.Contains("https://git.example.com/app.git", repositories.Output);
        Assert.Equal(0, rule.ExitCode);
        Assert.Equal(6, Assert.Single(JsonDocument.Parse(rules.Output).RootElement.EnumerateArray()).GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task Administrators_restricted_token_cannot_manage_users()
    {
        await using var app = new AggregatorApp();
        var environment = await app.CreateEnvironmentAsync("r2");
        var response = await app.CreateClient().PostAsJsonAsync(
            "/api/me/tokens", new { name = "ci", readOnly = false, environmentIds = new[] { environment.Id } }, Ct);
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("token").GetString()!;

        var result = await RunAsync(app, null, "", "--url", "http://localhost", "--token", token, "user", "list");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("limited to some environments", result.Error);
    }

    [Fact]
    public async Task Secrets_init_writes_the_environment_key_and_the_git_filter_works_through_the_aggregator()
    {
        await using var app = new AggregatorApp();
        var environment = await app.CreateEnvironmentAsync("prod");
        var key = await app.CreateClient().PostAsync($"/api/environments/{environment.Id}/age-keys", null, Ct);
        var recipient = (await key.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("recipient").GetString()!;
        var response = await app.CreateClient().PostAsJsonAsync("/api/me/tokens", new { name = "git", readOnly = false, secrets = true }, Ct);
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("token").GetString()!;
        await RunAsync(app, null, token, "login", "http://localhost", "--with-token");
        var repository = Directory.CreateTempSubdirectory("cf-git-").FullName;
        try
        {
            Git(repository, "init", "-q", "-b", "main");

            var init = await RunInAsync(app, null, "", repository, null, null, "secrets", "init", "-e", "prod", "--recovery", "age1lggyhqrw2nlhcxprm67z43rta597azn8gknawjehu9d9dl0jq3yqqvfafg");

            Assert.True(init.ExitCode == 0, init.Error);
            Assert.Contains($"age: {recipient},age1lggyhqrw2nlhcxprm67z43rta597azn8gknawjehu9d9dl0jq3yqqvfafg", await File.ReadAllTextAsync(Path.Combine(repository, ".sops.yaml"), Ct));
            Assert.Contains(".env filter=forest diff=forest", await File.ReadAllTextAsync(Path.Combine(repository, ".gitattributes"), Ct));
            Assert.Contains("forest.context=localhost", Git(repository, "config", "--list"));
            Assert.Contains("secrets check --staged", await File.ReadAllTextAsync(Path.Combine(repository, ".git", "hooks", "pre-commit"), Ct));

            // git's side of the filter protocol: clean what git add stores, then smudge it back for checkout.
            var requests = new MemoryStream();
            var git = new Cli.Secrets.PktLineWriter(requests);
            await git.WriteTextListAsync(["git-filter-client", "version=2"], Ct);
            await git.WriteTextListAsync(["capability=clean", "capability=smudge"], Ct);
            await git.WriteTextListAsync(["command=clean", "pathname=app/.env"], Ct);
            await git.WriteContentAsync("TOKEN=s3cret\n"u8.ToArray(), Ct);
            requests.Position = 0;
            var responses = new MemoryStream();
            var filter = await RunInAsync(app, null, "", repository, requests, responses, "git", "filter-process");

            Assert.True(filter.ExitCode == 0, filter.Error);
            responses.Position = 0;
            var reader = new Cli.Secrets.PktLineReader(responses);
            await reader.ReadTextListAsync(Ct);
            await reader.ReadTextListAsync(Ct);
            Assert.Equal(["status=success"], await reader.ReadTextListAsync(Ct));
            var encrypted = await reader.ReadContentAsync(Ct);
            Assert.Contains($"recipient={recipient}", Encoding.UTF8.GetString(encrypted));

            var decrypted = await app.CreateClient().PostAsJsonAsync("/api/sops/decrypt", new { path = "app/.env", content = encrypted }, Ct);
            Assert.Equal("TOKEN=s3cret", Encoding.UTF8.GetString((await decrypted.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("content").GetBytesFromBase64()));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    private static string Git(string directory, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        return output;
    }

    private Task<CliResult> RunAsync(AggregatorApp app, IBrowser? browser, string input, params string[] args) =>
        RunInAsync(app, browser, input, null, null, null, args);

    /// <summary>Runs the CLI in a directory (a git work tree), with byte streams for the git filter protocol.</summary>
    private async Task<CliResult> RunInAsync(
        AggregatorApp app, IBrowser? browser, string input, string? workingDirectory, Stream? stdin, Stream? stdout, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var host = new CliHost(
            output,
            error,
            new StringReader(input),
            _ => null,
            new ConfigStore(configDirectory),
            new ForestApiFactory(() => app.Server.CreateHandler()),
            browser ?? new ApprovingBrowser(app),
            TimeProvider.System,
            standardInput: stdin,
            standardOutput: stdout,
            workingDirectory: workingDirectory);
        var code = await ForestCli.Build(host).Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Ct);
        return new CliResult(code, output.ToString(), error.ToString());
    }

    private static async Task<string> CreateTokenAsync(AggregatorApp app, bool readOnly = false)
    {
        var response = await app.CreateClient().PostAsJsonAsync("/api/me/tokens", new { name = "cli", readOnly }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("token").GetString()!;
    }

    private static async Task CreateStackAsync(AggregatorApp app, FakeAgent agent)
    {
        app.Git.Snapshot = new GitSnapshot(
            CommitSha.Parse("0123456789abcdef0123456789abcdef01234567"),
            [new SourceFile(StackFilePath.Parse("compose.yaml"), Encoding.UTF8.GetBytes("services:\n  web:\n    image: nginx:1.27\n"), IsExecutable: false)],
            []);
        var client = app.CreateClient();
        var repository = await client.PostAsJsonAsync("/api/repositories", new { name = "infra", url = "https://git.example.com/infra.git" }, Ct);
        var repositoryId = (await repository.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("id").GetGuid();
        var stack = await client.PostAsJsonAsync(
            $"/api/environments/{agent.EnvironmentId}/stacks",
            new { name = "web", repositoryId, branch = "main", composePath = "compose.yaml" },
            Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, stack);
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);

    /// <summary>The person in the browser: approves the login in the panel, then the panel sends the browser back to the CLI.</summary>
    private sealed class ApprovingBrowser(AggregatorApp app) : IBrowser
    {
        public bool TryOpen(Uri page)
        {
            var query = page.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            _ = Task.Run(async () =>
            {
                var approve = await app.CreateClient().PostAsJsonAsync(
                    $"/api/cli/authorizations/{query["code"]}/approve", new { name = "forest test", readOnly = false });
                approve.EnsureSuccessStatusCode();
                using var loopback = new HttpClient();
                await loopback.GetAsync($"http://127.0.0.1:{query["port"]}/done?result=approved");
            });
            return true;
        }
    }
}
