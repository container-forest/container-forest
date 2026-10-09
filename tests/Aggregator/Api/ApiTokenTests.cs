using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace ContainerForest.Aggregator.Api.Tests;

public class ApiTokenTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Token_acts_as_its_user()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        var token = await CreateTokenAsync(app.CreateClientAs("alice"), new { name = "laptop", readOnly = false });

        var me = await WithToken(app, token).GetFromJsonAsync<JsonElement>("/api/me", AggregatorApp.Json, Ct);

        Assert.Equal("alice", me.GetProperty("userName").GetString());
    }

    [Fact]
    public async Task Created_token_is_shown_once_and_listed_by_its_hint()
    {
        await using var app = new AggregatorApp();
        var client = app.CreateClient();
        var token = await CreateTokenAsync(client, new { name = "laptop", readOnly = false });

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/me/tokens", AggregatorApp.Json, Ct);

        var entry = Assert.Single(listed.EnumerateArray());
        Assert.Equal("laptop", entry.GetProperty("name").GetString());
        Assert.StartsWith(entry.GetProperty("hint").GetString()!, token);
        Assert.DoesNotContain(token, listed.GetRawText());
    }

    [Fact]
    public async Task Unknown_token_is_unauthorized()
    {
        await using var app = new AggregatorApp();

        var response = await WithToken(app, "cf_pat_unknown").GetAsync("/api/me", Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, response);
    }

    [Fact]
    public async Task Revoked_token_is_unauthorized()
    {
        await using var app = new AggregatorApp();
        var client = app.CreateClient();
        var token = await CreateTokenAsync(client, new { name = "laptop", readOnly = false });
        var id = (await client.GetFromJsonAsync<JsonElement>("/api/me/tokens", AggregatorApp.Json, Ct))[0].GetProperty("id").GetGuid();

        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, client.DeleteAsync($"/api/me/tokens/{id}", Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, WithToken(app, token).GetAsync("/api/me", Ct));
    }

    [Fact]
    public async Task Read_only_token_reads_but_changes_nothing()
    {
        await using var app = new AggregatorApp();
        var token = await CreateTokenAsync(app.CreateClient(), new { name = "monitoring", readOnly = true });
        var client = WithToken(app, token);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, client.GetAsync("/api/environments", Ct));
        var create = await client.PostAsJsonAsync("/api/environments", new { name = "sneaky", isPrivate = false }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, create);
        Assert.Empty(await app.CreateClient().GetFromJsonAsync<JsonElement[]>("/api/environments", AggregatorApp.Json, Ct) ?? []);
    }

    [Fact]
    public async Task Read_only_token_can_revoke_itself()
    {
        await using var app = new AggregatorApp();
        var token = await CreateTokenAsync(app.CreateClient(), new { name = "cli", readOnly = true });
        var client = WithToken(app, token);

        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, client.DeleteAsync("/api/me/tokens/current", Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, client.GetAsync("/api/me", Ct));
    }

    [Fact]
    public async Task Token_cannot_create_tokens_or_change_the_password()
    {
        await using var app = new AggregatorApp();
        var client = WithToken(app, await CreateTokenAsync(app.CreateClient(), new { name = "cli", readOnly = false }));

        var token = await client.PostAsJsonAsync("/api/me/tokens", new { name = "wider", readOnly = false }, Ct);
        var password = await client.PostAsJsonAsync(
            "/api/auth/password", new { currentPassword = AggregatorApp.AdminPassword, newPassword = "another long passphrase" }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, token);
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, password);
    }

    [Fact]
    public async Task Restricted_token_sees_only_its_environments_and_is_no_administrator()
    {
        await using var app = new AggregatorApp();
        var inside = await app.CreateEnvironmentAsync("inside");
        await app.CreateEnvironmentAsync("outside");
        var token = await CreateTokenAsync(app.CreateClient(), new { name = "ci", readOnly = false, environmentIds = new[] { inside.Id } });
        var client = WithToken(app, token);

        var environments = await client.GetFromJsonAsync<JsonElement[]>("/api/environments", AggregatorApp.Json, Ct);
        var users = await client.GetAsync("/api/users", Ct);

        Assert.Equal(inside.Id, Assert.Single(environments!).GetProperty("id").GetGuid());
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, users);
    }

    [Fact]
    public async Task Token_of_a_deactivated_user_is_forbidden()
    {
        await using var app = new AggregatorApp();
        var alice = await app.CreateUserAsync("alice", isAdmin: false);
        var token = await CreateTokenAsync(app.CreateClientAs("alice"), new { name = "laptop", readOnly = false });

        await HttpAssert.StatusAsync(HttpStatusCode.OK, app.CreateClient().PostAsync($"/api/users/{alice}/deactivate", null, Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, WithToken(app, token).GetAsync("/api/me", Ct));
    }

    [Fact]
    public async Task Administrator_revokes_another_users_token()
    {
        await using var app = new AggregatorApp();
        var alice = await app.CreateUserAsync("alice", isAdmin: false);
        var token = await CreateTokenAsync(app.CreateClientAs("alice"), new { name = "laptop", readOnly = false });
        var admin = app.CreateClient();
        var id = (await admin.GetFromJsonAsync<JsonElement>($"/api/users/{alice}/tokens", AggregatorApp.Json, Ct))[0].GetProperty("id").GetGuid();

        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, admin.DeleteAsync($"/api/users/{alice}/tokens/{id}", Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, WithToken(app, token).GetAsync("/api/me", Ct));
    }

    [Fact]
    public async Task Token_cannot_open_the_live_hub()
    {
        await using var app = new AggregatorApp();
        var token = await CreateTokenAsync(app.CreateClient(), new { name = "cli", readOnly = false });
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/live"), options =>
            {
                options.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync(Ct));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
    }

    private static HttpClient WithToken(AggregatorApp app, string token)
    {
        var client = app.CreateClientAs(null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<string> CreateTokenAsync(HttpClient session, object request)
    {
        var response = await session.PostAsJsonAsync("/api/me/tokens", request, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("token").GetString()!;
    }
}
