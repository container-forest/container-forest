using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ContainerForest.Aggregator.Api.Tests;

public class AuditTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sign_ins_are_recorded_failed_ones_with_the_name_they_gave()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        var browser = app.CreateBrowserClient();

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, LoginAsync(browser, "alice", "not the password at all"));
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, LoginAsync(browser, "nobody", "not the password at all"));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(browser, "alice", AggregatorApp.DefaultPassword));
        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, browser.PostAsync("/api/auth/logout", null, Ct));

        var entries = await ListAsync(app.CreateClient(), "category=Account");
        Assert.Equal(
            [
                ("SignedOut", "Succeeded", "alice", "Panel"),
                ("SignedIn", "Succeeded", "alice", "Panel"),
                ("SignInFailed", "Failed", "nobody", "Anonymous"),
                ("SignInFailed", "Failed", "alice", "Anonymous"),
            ],
            entries.Select(e => (Text(e, "action"), Text(e, "outcome"), Text(e, "actorName"), Text(e, "channel"))));
        Assert.Equal("Password. Wrong password.", Text(entries[3], "details"));
        Assert.Equal("Password. Unknown user name.", Text(entries[2], "details"));

        // Neither password appears anywhere in the log.
        var raw = await app.CreateClient().GetStringAsync("/api/audit", Ct);
        Assert.DoesNotContain(AggregatorApp.DefaultPassword, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("not the password at all", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task User_management_is_recorded_with_who_did_it()
    {
        await using var app = new AggregatorApp();
        var aliceId = await app.CreateUserAsync("alice", isAdmin: false);
        await HttpAssert.StatusAsync(
            HttpStatusCode.OK, app.CreateClient().PostAsync($"/api/users/{aliceId}/deactivate", null, Ct));

        var entries = await ListAsync(app.CreateClient(), "category=Users");

        Assert.Equal(["UserDeactivated", "UserCreated"], entries.Select(e => Text(e, "action")));
        Assert.All(entries, e => Assert.Equal(AggregatorApp.AdminUserName, Text(e, "actorName")));
        Assert.All(entries, e => Assert.Equal("alice", Text(e, "targetName")));
        Assert.Equal(aliceId.ToString(), Text(entries[0], "targetId"));
    }

    [Fact]
    public async Task A_refused_attempt_is_recorded_as_denied()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);

        await HttpAssert.StatusAsync(
            HttpStatusCode.Forbidden,
            app.CreateClientAs("alice").PostAsJsonAsync(
                "/api/users", new { userName = "mallory", password = AggregatorApp.DefaultPassword, isAdmin = true }, Ct));

        var entry = Assert.Single(await ListAsync(app.CreateClient(), "outcome=Denied"));
        Assert.Equal(("UserCreated", "alice", "mallory"), (Text(entry, "action"), Text(entry, "actorName"), Text(entry, "targetName")));
    }

    [Fact]
    public async Task Only_administrators_read_the_log()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, app.CreateClientAs("alice").GetAsync("/api/audit", Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, app.CreateClientAs(null).GetAsync("/api/audit", Ct));
    }

    [Fact]
    public async Task The_log_is_filtered_searched_and_paged()
    {
        await using var app = new AggregatorApp();
        foreach (var name in new[] { "alice", "bob", "carol" })
        {
            await app.CreateUserAsync(name, isAdmin: false);
        }

        var environment = await app.CreateEnvironmentAsync("prod");
        var admin = app.CreateClient();

        var page = await admin.GetFromJsonAsync<JsonElement>("/api/audit?category=Users&pageSize=2&page=2", AggregatorApp.Json, Ct);
        var searched = await ListAsync(admin, "search=BO");
        var inEnvironment = await ListAsync(admin, $"environmentId={environment.Id}");

        Assert.Equal((3, 2, 1), (page.GetProperty("total").GetInt32(), page.GetProperty("page").GetInt32(), page.GetProperty("entries").GetArrayLength()));
        Assert.Equal("alice", Text(page.GetProperty("entries")[0], "targetName"));
        Assert.Equal(["bob"], searched.Select(e => Text(e, "targetName")));
        Assert.Equal([("EnvironmentCreated", "prod")], inEnvironment.Select(e => (Text(e, "action"), Text(e, "targetName"))));
    }

    [Fact]
    public async Task Tokens_are_recorded_and_requests_with_them_say_so()
    {
        await using var app = new AggregatorApp();
        var admin = app.CreateClient();
        var created = await admin.PostAsJsonAsync(
            "/api/me/tokens", new { name = "ci", readOnly = false, environmentIds = (Guid[]?)null, expiresAt = (DateTimeOffset?)null }, Ct);
        var token = (await created.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("token").GetString();

        var cli = app.CreateClientAs(null);
        cli.DefaultRequestHeaders.Authorization = new("Bearer", token);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, cli.PostAsJsonAsync("/api/environments", new { name = "staging", isPrivate = false }, Ct));

        var tokenEntry = Assert.Single(await ListAsync(admin, "category=Tokens"));
        var environmentEntry = Assert.Single(await ListAsync(admin, "action=EnvironmentCreated"));
        Assert.Equal(("ApiTokenCreated", "ci", "full access, no expiry"), (Text(tokenEntry, "action"), Text(tokenEntry, "targetName"), Text(tokenEntry, "details")));
        Assert.Equal(("ApiToken", "staging"), (Text(environmentEntry, "channel"), Text(environmentEntry, "targetName")));
        Assert.DoesNotContain(token!, await admin.GetStringAsync("/api/audit", Ct), StringComparison.Ordinal);
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string query)
    {
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/audit?{query}", AggregatorApp.Json, Ct);
        return [.. page.GetProperty("entries").EnumerateArray()];
    }

    private static string? Text(JsonElement entry, string property) => entry.GetProperty(property).GetString();

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { userName, password, rememberMe = false }, Ct);
}
