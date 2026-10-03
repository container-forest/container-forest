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
    public async Task Editor_adds_a_repository_under_a_connection_of_their_environment_without_a_token()
    {
        await using var app = new AggregatorApp();
        var aliceId = await app.CreateUserAsync("alice", isAdmin: false);
        await using var agent = await app.EnrollAndConnectAsync();
        var other = await app.CreateEnvironmentAsync("other");
        var admin = app.CreateClient();

        var created = await admin.PostAsJsonAsync(
            "/api/git-connections",
            new
            {
                name = "team",
                urlPrefix = "https://gitea.example.com/team/",
                accessToken = new { userName = "forest-team", token = "gitea_secret" },
                allEnvironments = false,
                environmentIds = new[] { agent.EnvironmentId },
            },
            Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, created);
        var body = await created.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("gitea_secret", body, StringComparison.Ordinal);
        var connection = JsonSerializer.Deserialize<ConnectionView>(body, AggregatorApp.Json)!;
        Assert.Equal((true, "forest-team"), (connection.HasAccessToken, connection.AccessUserName));

        await HttpAssert.StatusAsync(
            HttpStatusCode.Created,
            admin.PostAsJsonAsync($"/api/users/{aliceId}/role-assignments", new { environmentId = agent.EnvironmentId, stackId = (Guid?)null, role = "Editor" }, Ct));
        var alice = app.CreateClientAs("alice");

        var connections = (await alice.GetFromJsonAsync<ConnectionView[]>("/api/git-connections", AggregatorApp.Json, Ct))!;
        Assert.Equal([agent.EnvironmentId], Assert.Single(connections).EnvironmentIds);
        await HttpAssert.StatusAsync(
            HttpStatusCode.Forbidden,
            alice.PutAsJsonAsync($"/api/git-connections/{connection.Id}", new { name = "mine", urlPrefix = "https://gitea.example.com/", allEnvironments = true }, Ct));

        await HttpAssert.StatusAsync(
            HttpStatusCode.BadRequest,
            alice.PostAsJsonAsync("/api/repositories", new { name = "ledger", url = "https://gitea.example.com/billing/ledger.git", connectionId = connection.Id }, Ct));
        var repository = await alice.PostAsJsonAsync(
            "/api/repositories", new { name = "app", url = "https://gitea.example.com/team/app.git", connectionId = connection.Id }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, repository);
        var view = (await repository.Content.ReadFromJsonAsync<RepositoryView>(AggregatorApp.Json, Ct))!;
        Assert.Equal((connection.Id, "team", true), (view.ConnectionId, view.ConnectionName, view.CanManage));

        await HttpAssert.StatusAsync(
            HttpStatusCode.Created,
            alice.PostAsJsonAsync(
                $"/api/environments/{agent.EnvironmentId}/stacks", new { name = "app", repositoryId = view.Id, branch = "main", composePath = "compose.yaml" }, Ct));

        // The administrator may use the repository anywhere they may add stacks, but not where its connection is not allowed.
        await HttpAssert.StatusAsync(
            HttpStatusCode.NotFound,
            admin.PostAsJsonAsync(
                $"/api/environments/{other.Id}/stacks", new { name = "app", repositoryId = view.Id, branch = "main", composePath = "compose.yaml" }, Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, admin.DeleteAsync($"/api/git-connections/{connection.Id}", Ct));
    }

    private static async Task<Guid> CreateRepositoryAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/repositories", new { name, url = $"https://git.example.com/{name}.git" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, response);
        return (await response.Content.ReadFromJsonAsync<StackView>(AggregatorApp.Json, Ct))!.Id;
    }

    private sealed record RepositoryView(Guid Id, string Name, string Url, Guid? ConnectionId, string? ConnectionName, bool CanManage);

    private sealed record ConnectionView(Guid Id, string Name, bool HasAccessToken, string? AccessUserName, Guid[] EnvironmentIds);

    private sealed record StackView(
        Guid Id, Guid EnvironmentId, string? RepositoryName, string Branch, string ComposePath, bool CanDeploy, bool CanRestart);
}
