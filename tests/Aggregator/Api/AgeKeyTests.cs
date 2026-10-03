using System.Net;
using System.Net.Http.Json;
using System.Text;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;
using ContainerForest.Contracts.Agent;

namespace ContainerForest.Aggregator.Api.Tests;

public class AgeKeyTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Administrator_manages_keys_and_viewers_only_read_the_public_part()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        await using var agent = await app.EnrollAndConnectAsync();
        var admin = app.CreateClient();
        var alice = app.CreateClientAs("alice");
        var keys = $"/api/environments/{agent.EnvironmentId}/age-keys";

        var created = await admin.PostAsync(keys, null, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, created);
        var body = await created.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("AGE-SECRET-KEY", body, StringComparison.Ordinal);
        var key = System.Text.Json.JsonSerializer.Deserialize<KeyView>(body, AggregatorApp.Json)!;
        Assert.StartsWith("age1", key.Recipient, StringComparison.Ordinal);

        // A public environment gives everyone an implicit Viewer role.
        var listed = await alice.GetFromJsonAsync<KeyView[]>(keys, AggregatorApp.Json, Ct);
        Assert.Equal(key.Recipient, Assert.Single(listed!).Recipient);
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, alice.PostAsync(keys, null, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, alice.DeleteAsync($"{keys}/{key.Id}", Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, admin.GetAsync($"/api/environments/{Guid.NewGuid()}/age-keys", Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, admin.DeleteAsync($"{keys}/{key.Id}", Ct));
        Assert.Empty((await admin.GetFromJsonAsync<KeyView[]>(keys, AggregatorApp.Json, Ct))!);
    }

    [Fact]
    public async Task Encrypted_files_reach_the_agent_decrypted_and_marked_secret()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        var client = app.CreateClient();
        await HttpAssert.StatusAsync(HttpStatusCode.Created, client.PostAsync($"/api/environments/{agent.EnvironmentId}/age-keys", null, Ct));
        app.Git.Snapshot = new GitSnapshot(
            CommitSha.Parse(Sha),
            [
                File("compose.yaml", "services:\n  web:\n    image: nginx:1.27\n    env_file: .env\n"),
                File(".env", "encrypted for AGE-SECRET-KEY-1TEST1: TOKEN=s3cret\nsops_version=3.13.3\n"),
            ],
            []);
        var stackId = await CreateStackAsync(client, agent.EnvironmentId);

        var response = await client.PostAsJsonAsync($"/api/stacks/{stackId}/deployments", new { pull = false }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, response);

        var command = await agent.ReceiveAsync();
        var files = command.DeployStack.Files.ToDictionary(f => f.Path);
        Assert.False(files["compose.yaml"].Secret);
        Assert.True(files[".env"].Secret);
        Assert.Equal("TOKEN=s3cret", files[".env"].Content.ToStringUtf8());
        await agent.SendAsync(new AgentMessage { Result = new CommandResult { RequestId = command.RequestId } });
    }

    private static SourceFile File(string path, string content) =>
        new(StackFilePath.Parse(path), Encoding.UTF8.GetBytes(content), IsExecutable: false);

    private static async Task<Guid> CreateStackAsync(HttpClient client, Guid environmentId)
    {
        var repository = await client.PostAsJsonAsync("/api/repositories", new { name = "infra", url = "https://git.example.com/infra.git" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, repository);
        var repositoryId = (await repository.Content.ReadFromJsonAsync<KeyView>(AggregatorApp.Json, Ct))!.Id;
        var stack = await client.PostAsJsonAsync(
            $"/api/environments/{environmentId}/stacks", new { name = "web", repositoryId, branch = "main", composePath = "compose.yaml" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, stack);
        return (await stack.Content.ReadFromJsonAsync<KeyView>(AggregatorApp.Json, Ct))!.Id;
    }

    private sealed record KeyView(Guid Id, string? Recipient);
}
