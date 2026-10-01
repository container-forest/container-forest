using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContainerForest.Aggregator.Infrastructure.Accounts;

namespace ContainerForest.Aggregator.Api.Tests;

public class UserManagementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_created_user_can_sign_in()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        var browser = app.CreateBrowserClient();

        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(browser, "alice", AggregatorApp.DefaultPassword));

        var me = await browser.GetFromJsonAsync<JsonElement>("/api/me", AggregatorApp.Json, Ct);
        Assert.Equal("alice", me.GetProperty("userName").GetString());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task Users_are_listed_by_name()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("zed", isAdmin: false);
        await app.CreateUserAsync("bob", isAdmin: true);

        var users = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/users", AggregatorApp.Json, Ct);

        Assert.Equal(["admin", "bob", "zed"], users.EnumerateArray().Select(u => u.GetProperty("userName").GetString()));
    }

    [Fact]
    public async Task Regular_users_cannot_manage_users()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        var alice = app.CreateClientAs("alice");

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, alice.GetAsync("/api/users", Ct));
        await HttpAssert.StatusAsync(
            HttpStatusCode.Forbidden,
            alice.PostAsJsonAsync("/api/users", new { userName = "mallory", password = AggregatorApp.DefaultPassword, isAdmin = true }, Ct));
    }

    [Fact]
    public async Task Duplicate_name_is_a_conflict_and_a_short_password_is_rejected()
    {
        await using var app = new AggregatorApp();
        var admin = app.CreateClient();

        var duplicate = await admin.PostAsJsonAsync(
            "/api/users", new { userName = "ADMIN", password = AggregatorApp.DefaultPassword, isAdmin = false }, Ct);
        var shortPassword = await admin.PostAsJsonAsync("/api/users", new { userName = "bob", password = "short", isAdmin = false }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, duplicate);
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, shortPassword);
        var problem = await shortPassword.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        Assert.NotEmpty(problem.GetProperty("errors").EnumerateArray());
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, LoginAsync(app.CreateBrowserClient(), "bob", "short"));
    }

    [Fact]
    public async Task Easily_guessed_passwords_are_rejected_with_every_reason()
    {
        await using var app = new AggregatorApp();

        var response = await app.CreateClient().PostAsJsonAsync(
            "/api/users", new { userName = "bob", password = "bobbobbobbob", isAdmin = false }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, response);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        Assert.Equal(
            ["Passwords must not be repeated or sequential characters, such as 'aaaa…', '1234…' or 'qwerty…'.", "Passwords must not contain the user name."],
            problem.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_last_active_administrator_stays()
    {
        await using var app = new AggregatorApp();
        var admin = app.CreateClient();
        var adminId = (await admin.GetFromJsonAsync<JsonElement>("/api/me", AggregatorApp.Json, Ct)).GetProperty("id").GetGuid();

        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, admin.PostAsync($"/api/users/{adminId}/deactivate", null, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, admin.PutAsJsonAsync($"/api/users/{adminId}/admin", new { isAdmin = false }, Ct));
    }

    [Fact]
    public async Task Deactivation_ends_the_users_session()
    {
        await using var app = new AggregatorApp();
        var aliceId = await app.CreateUserAsync("alice", isAdmin: false);
        var browser = app.CreateBrowserClient();
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(browser, "alice", AggregatorApp.DefaultPassword));

        var deactivated = await app.CreateClient().PostAsync($"/api/users/{aliceId}/deactivate", null, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, deactivated);
        var body = await deactivated.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        Assert.False(body.GetProperty("isActive").GetBoolean());
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, browser.GetAsync("/api/me", Ct));
    }

    [Fact]
    public async Task Password_reset_ends_sessions_and_the_new_password_works()
    {
        await using var app = new AggregatorApp();
        var aliceId = await app.CreateUserAsync("alice", isAdmin: false);
        var browser = app.CreateBrowserClient();
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(browser, "alice", AggregatorApp.DefaultPassword));

        var reset = await app.CreateClient().PostAsJsonAsync(
            $"/api/users/{aliceId}/password", new { newPassword = "handed over by the admin" }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, reset);
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, browser.GetAsync("/api/me", Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(app.CreateBrowserClient(), "alice", "handed over by the admin"));
    }

    [Fact]
    public async Task Renamed_user_signs_in_with_the_new_name()
    {
        await using var app = new AggregatorApp();
        var aliceId = await app.CreateUserAsync("alice", isAdmin: false);

        var renamed = await app.CreateClient().PutAsJsonAsync($"/api/users/{aliceId}/user-name", new { userName = "alice.smith" }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, renamed);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(app.CreateBrowserClient(), "alice.smith", AggregatorApp.DefaultPassword));
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, LoginAsync(app.CreateBrowserClient(), "alice", AggregatorApp.DefaultPassword));
    }

    [Fact]
    public async Task Lockout_is_shown_and_can_be_lifted()
    {
        await using var app = new AggregatorApp();
        var aliceId = await app.CreateUserAsync("alice", isAdmin: false);
        var browser = app.CreateBrowserClient();
        for (var attempt = 0; attempt < AccountPolicy.MaxFailedSignIns; attempt++)
        {
            await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, LoginAsync(browser, "alice", "not the password"));
        }

        var locked = await app.CreateClient().GetFromJsonAsync<JsonElement>($"/api/users/{aliceId}", AggregatorApp.Json, Ct);
        var unlocked = await app.CreateClient().PostAsync($"/api/users/{aliceId}/unlock", null, Ct);

        Assert.Equal(JsonValueKind.String, locked.GetProperty("user").GetProperty("lockedOutUntil").ValueKind);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, unlocked);
        var body = await unlocked.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lockedOutUntil").ValueKind);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(browser, "alice", AggregatorApp.DefaultPassword));
    }

    [Fact]
    public async Task A_role_makes_a_private_environment_visible_until_it_is_revoked()
    {
        await using var app = new AggregatorApp();
        var aliceId = await app.CreateUserAsync("alice", isAdmin: false);
        var admin = app.CreateClient();
        var created = await admin.PostAsJsonAsync("/api/dev/environments", new { name = "prod", isPrivate = true }, Ct);
        var environmentId = (await created.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("id").GetGuid();
        var alice = app.CreateClientAs("alice");

        var granted = await admin.PostAsJsonAsync(
            $"/api/users/{aliceId}/role-assignments", new { environmentId, stackId = (Guid?)null, role = "Operator" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, granted);
        var assignment = await granted.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        var visible = await alice.GetFromJsonAsync<JsonElement>("/api/dev/environments", AggregatorApp.Json, Ct);

        await HttpAssert.StatusAsync(
            HttpStatusCode.NoContent,
            admin.DeleteAsync($"/api/users/{aliceId}/role-assignments/{assignment.GetProperty("id").GetGuid()}", Ct));
        var hidden = await alice.GetFromJsonAsync<JsonElement>("/api/dev/environments", AggregatorApp.Json, Ct);

        Assert.Equal("Environment", assignment.GetProperty("scope").GetString());
        Assert.Equal("prod", assignment.GetProperty("environmentName").GetString());
        Assert.Equal(1, visible.GetArrayLength());
        Assert.Equal(0, hidden.GetArrayLength());
    }

    [Fact]
    public async Task Duplicate_role_on_a_scope_is_a_conflict_and_unknown_ids_are_not_found()
    {
        await using var app = new AggregatorApp();
        var aliceId = await app.CreateUserAsync("alice", isAdmin: false);
        var admin = app.CreateClient();
        var environment = await app.CreateEnvironmentAsync("prod");
        var grant = new { environmentId = environment.Id, stackId = (Guid?)null, role = "Viewer" };

        await HttpAssert.StatusAsync(HttpStatusCode.Created, admin.PostAsJsonAsync($"/api/users/{aliceId}/role-assignments", grant, Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, admin.PostAsJsonAsync($"/api/users/{aliceId}/role-assignments", grant, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, admin.PostAsJsonAsync($"/api/users/{Guid.NewGuid()}/role-assignments", grant, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, admin.GetAsync($"/api/users/{Guid.NewGuid()}", Ct));
        await HttpAssert.StatusAsync(
            HttpStatusCode.NotFound,
            admin.PostAsJsonAsync(
                $"/api/users/{aliceId}/role-assignments", new { environmentId = environment.Id, stackId = Guid.NewGuid(), role = "Viewer" }, Ct));
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { userName, password, rememberMe = false }, Ct);
}
