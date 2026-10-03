using System.Net;
using System.Net.Http.Json;
using System.Text;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Tests;

public class RehydrationTests
{
    private const string Deployed = "0123456789abcdef0123456789abcdef01234567";
    private const string BranchHead = "fedcba9876543210fedcba9876543210fedcba98";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reboot_of_the_host_redeploys_the_running_commit_of_stacks_with_secrets()
    {
        await using var app = new AggregatorApp();
        var agent = await app.EnrollAndConnectAsync("boot-1");
        var client = app.CreateClient();
        await HttpAssert.StatusAsync(HttpStatusCode.Created, client.PostAsync($"/api/environments/{agent.EnvironmentId}/age-keys", null, Ct));
        var withSecrets = await CreateStackAsync(client, agent.EnvironmentId, "web");
        var withoutSecrets = await CreateStackAsync(client, agent.EnvironmentId, "plain");

        await DeployAsync(app, agent, withSecrets, Snapshot(Deployed, secret: true));
        await DeployAsync(app, agent, withoutSecrets, Snapshot(Deployed, secret: false));

        // The branch moves on; rehydration must still deploy what runs, not the new head.
        app.Git.Snapshot = Snapshot(BranchHead, secret: true);
        await agent.DisposeAsync();
        await app.WaitUntilOnlineAsync(agent.EnvironmentId, online: false);
        await using var rebooted = await FakeAgent.ConnectAsync(app.Gateway, agent.Enrollment, "boot-2");

        var command = await rebooted.ReceiveAsync();
        Assert.Equal("web", command.DeployStack.StackName);
        Assert.Equal(Deployed, command.DeployStack.CommitSha);
        Assert.True(command.DeployStack.Files.Single(f => f.Path == ".env").Secret);
        Assert.Equal(Deployed, app.Git.ReadCommits[^1]);
        await rebooted.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });

        var history = await WaitForFinishedAsync(client, withSecrets);
        Assert.Equal(("Rehydrate", "Succeeded", Deployed), (history[0].Trigger, history[0].Status, history[0].Commit));
        Assert.Single(await client.GetFromJsonAsync<DeploymentView[]>($"/api/stacks/{withoutSecrets}/deployments", AggregatorApp.Json, Ct) ?? []);
    }

    [Fact]
    public async Task Reconnect_without_a_reboot_redeploys_nothing()
    {
        await using var app = new AggregatorApp();
        var agent = await app.EnrollAndConnectAsync("boot-1");
        var client = app.CreateClient();
        await HttpAssert.StatusAsync(HttpStatusCode.Created, client.PostAsync($"/api/environments/{agent.EnvironmentId}/age-keys", null, Ct));
        var stack = await CreateStackAsync(client, agent.EnvironmentId, "web");
        await DeployAsync(app, agent, stack, Snapshot(Deployed, secret: true));

        await agent.DisposeAsync();
        await app.WaitUntilOnlineAsync(agent.EnvironmentId, online: false);
        await using var reconnected = await FakeAgent.ConnectAsync(app.Gateway, agent.Enrollment, "boot-1");
        await app.WaitUntilOnlineAsync(agent.EnvironmentId);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);

        Assert.Single(await client.GetFromJsonAsync<DeploymentView[]>($"/api/stacks/{stack}/deployments", AggregatorApp.Json, Ct) ?? []);
    }

    private static GitSnapshot Snapshot(string sha, bool secret) => new(
        CommitSha.Parse(sha),
        [
            File("compose.yaml", "services:\n  web:\n    image: nginx:1.27\n    env_file: .env\n"),
            File(".env", secret ? "encrypted for AGE-SECRET-KEY-1TEST1: TOKEN=s3cret\nsops_version=3.13.3\n" : "TOKEN=plain\n"),
        ],
        []);

    private static SourceFile File(string path, string content) =>
        new(StackFilePath.Parse(path), Encoding.UTF8.GetBytes(content), IsExecutable: false);

    private static async Task DeployAsync(AggregatorApp app, FakeAgent agent, Guid stackId, GitSnapshot snapshot)
    {
        app.Git.Snapshot = snapshot;
        var client = app.CreateClient();
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, client.PostAsJsonAsync($"/api/stacks/{stackId}/deployments", new { pull = false }, Ct));
        var command = await agent.ReceiveAsync();
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });
        await WaitForFinishedAsync(client, stackId);
    }

    private static async Task<DeploymentView[]> WaitForFinishedAsync(HttpClient client, Guid stackId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var history = await client.GetFromJsonAsync<DeploymentView[]>($"/api/stacks/{stackId}/deployments", AggregatorApp.Json, Ct);
            if (history is [{ Status: "Succeeded" or "Failed" }, ..])
            {
                return history;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"No finished deployment of stack {stackId}.");
    }

    private static async Task<Guid> CreateStackAsync(HttpClient client, Guid environmentId, string name)
    {
        var repository = await client.PostAsJsonAsync("/api/repositories", new { name = $"repo-{name}", url = "https://git.example.com/infra.git" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, repository);
        var repositoryId = (await repository.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
        var stack = await client.PostAsJsonAsync(
            $"/api/environments/{environmentId}/stacks", new { name, repositoryId, branch = "main", composePath = "compose.yaml" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, stack);
        return (await stack.Content.ReadFromJsonAsync<IdView>(AggregatorApp.Json, Ct))!.Id;
    }

    private sealed record IdView(Guid Id);

    private sealed record DeploymentView(Guid Id, string Status, string Trigger, string? Commit);
}
