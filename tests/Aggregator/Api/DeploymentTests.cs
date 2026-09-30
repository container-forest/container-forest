using System.Net;
using System.Net.Http.Json;
using System.Text;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Tests;

public class DeploymentTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stack_is_deployed_from_git_through_the_agent()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        app.Git.Snapshot = Snapshot(("docker-compose.yml", "services:\n  web:\n    image: nginx\n"), ("conf/site.conf", "x"));
        var stack = await CreateStackAsync(app, agent, "apps/web/docker-compose.yml");

        var response = await app.CreateClient().PostAsJsonAsync($"/api/dev/stacks/{stack.Id}/deployments", new { pull = true }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, response);
        var queued = await response.Content.ReadFromJsonAsync<DeploymentView>(AggregatorApp.Json, Ct);

        var command = await agent.ReceiveAsync();
        Assert.Equal(ControllerMessage.BodyOneofCase.DeployStack, command.BodyCase);
        Assert.Equal("web", command.DeployStack.StackName);
        Assert.Equal(Sha, command.DeployStack.CommitSha);
        Assert.Equal(PullPolicy.Always, command.DeployStack.Pull);
        Assert.Equal(["compose.yaml", "conf/site.conf"], command.DeployStack.Files.Select(f => f.Path));

        await agent.SendAsync(new AgentMessage { Progress = new CommandProgress { RequestId = command.RequestId, Line = "Container web-web-1 Started" } });
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });

        var deployment = await WaitUntilFinishedAsync(app, queued!.Id);
        Assert.Equal("Succeeded", deployment.Status);
        Assert.Equal(Sha, deployment.Commit);
        Assert.Contains(deployment.Log, l => l.Text == "Container web-web-1 Started");
        Assert.Contains(deployment.Log, l => l.Level == "Warning" && l.Text.Contains("nginx", StringComparison.Ordinal));

        var stacks = await app.CreateClient().GetFromJsonAsync<StackView[]>(
            $"/api/dev/environments/{agent.EnvironmentId}/stacks", AggregatorApp.Json, Ct);
        Assert.Equal(Sha, stacks!.Single().DeployedCommit);
    }

    [Fact]
    public async Task Agent_error_fails_the_deployment_with_its_message()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        app.Git.Snapshot = Snapshot(("compose.yaml", "services:\n  web:\n    image: nginx:1.27\n"));
        var stack = await CreateStackAsync(app, agent, "compose.yaml");

        var queued = await QueueAsync(app, stack.Id);
        var command = await agent.ReceiveAsync();
        await agent.SendAsync(new AgentMessage
        {
            Result = new CommandResult
            {
                RequestId = command.RequestId,
                Error = new CommandError { Code = ErrorCode.CommandFailed, Message = "pull access denied for nginx" },
            },
        });

        var deployment = await WaitUntilFinishedAsync(app, queued.Id);
        Assert.Equal("Failed", deployment.Status);
        Assert.Equal("pull access denied for nginx", deployment.Error);
    }

    [Fact]
    public async Task Failing_step_stops_before_the_agent()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        app.Git.Snapshot = Snapshot(("compose.yaml", "services:\n  api:\n    build: .\n"));
        var stack = await CreateStackAsync(app, agent, "compose.yaml");

        var queued = await QueueAsync(app, stack.Id);

        var deployment = await WaitUntilFinishedAsync(app, queued.Id);
        Assert.Equal("Failed", deployment.Status);
        Assert.Contains("build", deployment.Error);
    }

    [Fact]
    public async Task Second_deployment_of_a_running_stack_is_a_conflict()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        app.Git.Snapshot = Snapshot(("compose.yaml", "services:\n  web:\n    image: nginx:1.27\n"));
        var stack = await CreateStackAsync(app, agent, "compose.yaml");

        await QueueAsync(app, stack.Id);
        await agent.ReceiveAsync();

        var second = await app.CreateClient().PostAsJsonAsync($"/api/dev/stacks/{stack.Id}/deployments", new { pull = false }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, second);
    }

    [Fact]
    public async Task Viewer_may_see_the_history_but_not_deploy()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        await using var agent = await app.EnrollAndConnectAsync();
        var stack = await CreateStackAsync(app, agent, "compose.yaml");
        var alice = app.CreateClientAs("alice");

        // Public environments give an implicit Viewer role.
        await HttpAssert.StatusAsync(HttpStatusCode.OK, alice.GetAsync($"/api/dev/stacks/{stack.Id}/deployments", Ct));
        await HttpAssert.StatusAsync(
            HttpStatusCode.Forbidden, alice.PostAsJsonAsync($"/api/dev/stacks/{stack.Id}/deployments", new { pull = false }, Ct));
        await HttpAssert.StatusAsync(
            HttpStatusCode.Forbidden,
            alice.PostAsJsonAsync("/api/dev/repositories", new { name = "mine", url = "https://git.example.com/mine.git" }, Ct));
    }

    private static GitSnapshot Snapshot(params (string Path, string Content)[] files) => new(
        CommitSha.Parse(Sha),
        files.Select(f => new SourceFile(StackFilePath.Parse(f.Path), Encoding.UTF8.GetBytes(f.Content), IsExecutable: false)).ToList(),
        []);

    private static async Task<StackView> CreateStackAsync(AggregatorApp app, FakeAgent agent, string composePath)
    {
        var client = app.CreateClient();
        var repository = await client.PostAsJsonAsync(
            "/api/dev/repositories", new { name = $"infra-{Guid.NewGuid():N}", url = "https://git.example.com/infra.git" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, repository);
        var repositoryId = (await repository.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;

        var stack = await client.PostAsJsonAsync(
            $"/api/dev/environments/{agent.EnvironmentId}/stacks",
            new { name = "web", repositoryId, branch = "main", composePath },
            Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, stack);
        return (await stack.Content.ReadFromJsonAsync<StackView>(AggregatorApp.Json, Ct))!;
    }

    private static async Task<DeploymentView> QueueAsync(AggregatorApp app, Guid stackId)
    {
        var response = await app.CreateClient().PostAsJsonAsync($"/api/dev/stacks/{stackId}/deployments", new { pull = false }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, response);
        return (await response.Content.ReadFromJsonAsync<DeploymentView>(AggregatorApp.Json, Ct))!;
    }

    private static async Task<DeploymentView> WaitUntilFinishedAsync(AggregatorApp app, Guid deploymentId)
    {
        var client = app.CreateClient();
        DeploymentView? deployment = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            deployment = await client.GetFromJsonAsync<DeploymentView>($"/api/dev/deployments/{deploymentId}", AggregatorApp.Json, Ct);
            if (deployment!.Status is "Succeeded" or "Failed" or "Cancelled")
            {
                return deployment;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail($"Deployment is still {deployment?.Status}. Log: {string.Join(" | ", deployment?.Log.Select(l => l.Text) ?? [])}");
        return deployment!;
    }

    private sealed record IdView(Guid Id);

    private sealed record StackView(Guid Id, string Name, string? DeployedCommit);

    private sealed record DeploymentView(Guid Id, string Status, string? Commit, string? Error, LogLineView[] Log);

    private sealed record LogLineView(string Level, string Text);
}
