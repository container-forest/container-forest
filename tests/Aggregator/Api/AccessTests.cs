using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ContainerForest.Aggregator.Api.Tests;

public class AccessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Me_returns_the_signed_in_user()
    {
        await using var app = new AggregatorApp();

        var me = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/me", AggregatorApp.Json, Ct);

        Assert.Equal(AggregatorApp.AdminUserName, me.GetProperty("userName").GetString());
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task Anonymous_request_is_unauthorized()
    {
        await using var app = new AggregatorApp();

        var response = await app.CreateClientAs(null).GetAsync("/api/me", Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, response);
    }

    [Fact]
    public async Task Regular_user_cannot_create_environments()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);

        var response = await app.CreateClientAs("alice").PostAsJsonAsync(
            "/api/dev/environments", new { name = "prod", isPrivate = true }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task Private_environment_is_hidden_from_users_without_a_role()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        var created = await app.CreateClient().PostAsJsonAsync("/api/dev/environments", new { name = "prod", isPrivate = true }, Ct);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("id").GetGuid();
        var alice = app.CreateClientAs("alice");

        var listed = await alice.GetFromJsonAsync<JsonElement>("/api/dev/environments", AggregatorApp.Json, Ct);
        var containers = await alice.GetAsync($"/api/dev/environments/{id}/containers", Ct);

        Assert.Equal(0, listed.GetArrayLength());
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, containers);
    }
}
