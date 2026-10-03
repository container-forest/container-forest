using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ContainerForest.Aggregator.Api.Tests;

public class StackEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stack_is_created_read_and_pointed_at_another_source()
    {
        await using var app = new AggregatorApp();
        await using var agent = await app.EnrollAndConnectAsync();
        var client = app.CreateClient();
        var infra = await CreateRepositoryAsync(client, "infra");
        var apps = await CreateRepositoryAsync(client, "apps");

        var created = await client.PostAsJsonAsync(
            $"/api/environments/{agent.EnvironmentId}/stacks",
            new { name = "web", repositoryId = infra, branch = "main", composePath = "web/compose.yaml" },
            Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, created);
        var stack = (await created.Content.ReadFromJsonAsync<StackView>(AggregatorApp.Json, Ct))!;
        Assert.Equal((agent.EnvironmentId, "infra", true), (stack.EnvironmentId, stack.RepositoryName, stack.CanDeploy));

        var changed = await client.PutAsJsonAsync(
            $"/api/stacks/{stack.Id}/source", new { repositoryId = apps, branch = "release", composePath = "compose.yaml" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, changed);

        var read = (await client.GetFromJsonAsync<StackView>($"/api/stacks/{stack.Id}", AggregatorApp.Json, Ct))!;
        Assert.Equal(("apps", "release", "compose.yaml"), (read.RepositoryName, read.Branch, read.ComposePath));
        var listed = (await client.GetFromJsonAsync<StackView[]>($"/api/environments/{agent.EnvironmentId}/stacks", AggregatorApp.Json, Ct))!;
        Assert.Equal(stack.Id, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task Viewer_reads_stacks_but_neither_repositories_nor_source_changes()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        await using var agent = await app.EnrollAndConnectAsync();
        var admin = app.CreateClient();
        var infra = await CreateRepositoryAsync(admin, "infra");
        var created = await admin.PostAsJsonAsync(
            $"/api/environments/{agent.EnvironmentId}/stacks", new { name = "web", repositoryId = infra, branch = "main", composePath = "compose.yaml" }, Ct);
        var stackId = (await created.Content.ReadFromJsonAsync<StackView>(AggregatorApp.Json, Ct))!.Id;
        var alice = app.CreateClientAs("alice");

        // Public environments give an implicit Viewer role.
        var stack = (await alice.GetFromJsonAsync<StackView>($"/api/stacks/{stackId}", AggregatorApp.Json, Ct))!;
        Assert.Equal((false, false), (stack.CanDeploy, stack.CanRestart));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, alice.GetAsync("/api/repositories", Ct));
        await HttpAssert.StatusAsync(
            HttpStatusCode.Forbidden,
            alice.PutAsJsonAsync($"/api/stacks/{stackId}/source", new { repositoryId = infra, branch = "dev", composePath = "compose.yaml" }, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, alice.GetAsync($"/api/stacks/{Guid.NewGuid()}", Ct));
    }

    [Fact]
    public async Task Access_token_is_write_only_and_stays_on_its_host()
    {
        await using var app = new AggregatorApp();
        var client = app.CreateClient();

        var created = await client.PostAsJsonAsync(
            "/api/repositories",
            new { name = "private", url = "https://git.example.com/team/private.git", accessToken = new { userName = (string?)null, token = "ghp_secret" } },
            Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, created);
        var body = await created.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("ghp_secret", body, StringComparison.Ordinal);
        var repository = JsonSerializer.Deserialize<RepositoryView>(body, AggregatorApp.Json)!;
        Assert.Equal((true, null), (repository.HasAccessToken, repository.AccessUserName));

        await HttpAssert.StatusAsync(
            HttpStatusCode.BadRequest,
            client.PutAsJsonAsync($"/api/repositories/{repository.Id}", new { name = "private", url = "https://evil.example.com/private.git" }, Ct));

        var replaced = await client.PutAsJsonAsync(
            $"/api/repositories/{repository.Id}/access-token", new { userName = "gitlab+deploy-token-1", token = "gldt-secret" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, replaced);
        Assert.Equal("gitlab+deploy-token-1", (await replaced.Content.ReadFromJsonAsync<RepositoryView>(AggregatorApp.Json, Ct))!.AccessUserName);

        var removed = await client.DeleteAsync($"/api/repositories/{repository.Id}/access-token", Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, removed);
        Assert.False((await removed.Content.ReadFromJsonAsync<RepositoryView>(AggregatorApp.Json, Ct))!.HasAccessToken);

        var moved = await client.PutAsJsonAsync(
            $"/api/repositories/{repository.Id}", new { name = "moved", url = "https://other.example.com/private.git" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, moved);
        Assert.Equal("https://other.example.com/private.git", (await moved.Content.ReadFromJsonAsync<RepositoryView>(AggregatorApp.Json, Ct))!.Url);
    }

    private static async Task<Guid> CreateRepositoryAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/repositories", new { name, url = $"https://git.example.com/{name}.git" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, response);
        return (await response.Content.ReadFromJsonAsync<StackView>(AggregatorApp.Json, Ct))!.Id;
    }

    private sealed record RepositoryView(Guid Id, string Name, string Url, bool HasAccessToken, string? AccessUserName);

    private sealed record StackView(
        Guid Id, Guid EnvironmentId, string? RepositoryName, string Branch, string ComposePath, bool CanDeploy, bool CanRestart);
}
