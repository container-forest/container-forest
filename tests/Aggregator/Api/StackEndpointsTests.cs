using System.Net;
using System.Net.Http.Json;

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

    private static async Task<Guid> CreateRepositoryAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/repositories", new { name, url = $"https://git.example.com/{name}.git" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, response);
        return (await response.Content.ReadFromJsonAsync<StackView>(AggregatorApp.Json, Ct))!.Id;
    }

    private sealed record StackView(
        Guid Id, Guid EnvironmentId, string? RepositoryName, string Branch, string ComposePath, bool CanDeploy, bool CanRestart);
}
