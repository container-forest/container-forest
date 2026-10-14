using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContainerForest.Aggregator.Infrastructure.Accounts;

namespace ContainerForest.Aggregator.Api.Tests;

public class AuthTests
{
    private const string AlicePassword = "a passphrase of my own";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Signing_in_starts_a_session()
    {
        await using var app = new AggregatorApp();
        var browser = app.CreateBrowserClient();

        var login = await LoginAsync(browser, AggregatorApp.AdminUserName, AggregatorApp.AdminPassword);
        var me = await browser.GetAsync("/api/me", Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, login);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        Assert.Equal(AggregatorApp.AdminUserName, body.GetProperty("userName").GetString());
        Assert.True(body.GetProperty("isAdmin").GetBoolean());
        await HttpAssert.StatusAsync(HttpStatusCode.OK, me);
    }

    [Fact]
    public async Task Session_cookie_is_http_only_and_same_site_strict()
    {
        await using var app = new AggregatorApp();

        var login = await LoginAsync(app.CreateBrowserClient(), AggregatorApp.AdminUserName, AggregatorApp.AdminPassword);

        var cookie = SessionCookie(login);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remember_me_makes_the_cookie_persistent()
    {
        await using var app = new AggregatorApp();

        var login = await LoginAsync(app.CreateBrowserClient(), AggregatorApp.AdminUserName, AggregatorApp.AdminPassword, rememberMe: true);

        Assert.Contains("expires=", SessionCookie(login), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_password_is_unauthorized()
    {
        await using var app = new AggregatorApp();
        var browser = app.CreateBrowserClient();

        var login = await LoginAsync(browser, AggregatorApp.AdminUserName, "not the password");

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, login);
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, browser.GetAsync("/api/me", Ct));
    }

    [Fact]
    public async Task Unknown_user_gets_the_same_answer_as_a_wrong_password()
    {
        await using var app = new AggregatorApp();
        var browser = app.CreateBrowserClient();

        var unknown = await LoginAsync(browser, "nobody", AggregatorApp.AdminPassword);
        var wrongPassword = await LoginAsync(browser, AggregatorApp.AdminUserName, "not the password");

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, unknown);
        Assert.Equal(await DetailAsync(wrongPassword), await DetailAsync(unknown));
    }

    [Fact]
    public async Task Deactivated_user_cannot_sign_in()
    {
        await using var app = new AggregatorApp();
        var alice = await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, app.CreateClient().PostAsync($"/api/users/{alice}/deactivate", null, Ct));

        var login = await LoginAsync(app.CreateBrowserClient(), "alice", AlicePassword);

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, login);
    }

    [Fact]
    public async Task Too_many_failed_sign_ins_lock_the_account()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        var browser = app.CreateBrowserClient();
        for (var attempt = 0; attempt < AccountPolicy.MaxFailedSignIns; attempt++)
        {
            await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, LoginAsync(browser, "alice", "not the password"));
        }

        var login = await LoginAsync(browser, "alice", AlicePassword);

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, login);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        await using var app = new AggregatorApp();
        var browser = app.CreateBrowserClient();
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(browser, AggregatorApp.AdminUserName, AggregatorApp.AdminPassword));

        var logout = await browser.PostAsync("/api/auth/logout", null, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, logout);
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, browser.GetAsync("/api/me", Ct));
    }

    [Fact]
    public async Task Changing_the_password_ends_other_sessions_but_keeps_this_one()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        var laptop = app.CreateBrowserClient();
        var phone = app.CreateBrowserClient();
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(laptop, "alice", AlicePassword));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(phone, "alice", AlicePassword));

        var change = await laptop.PostAsJsonAsync(
            "/api/auth/password", new { currentPassword = AlicePassword, newPassword = "a brand new passphrase" }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, change);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, laptop.GetAsync("/api/me", Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, phone.GetAsync("/api/me", Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(app.CreateBrowserClient(), "alice", "a brand new passphrase"));
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        var browser = app.CreateBrowserClient();
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(browser, "alice", AlicePassword));

        var change = await browser.PostAsJsonAsync(
            "/api/auth/password", new { currentPassword = "not the password", newPassword = "a brand new passphrase" }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, change);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(app.CreateBrowserClient(), "alice", AlicePassword));
    }

    [Fact]
    public async Task Short_new_password_is_rejected_with_the_reason()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        var browser = app.CreateBrowserClient();
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(browser, "alice", AlicePassword));

        var change = await browser.PostAsJsonAsync(
            "/api/auth/password", new { currentPassword = AlicePassword, newPassword = "too short" }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, change);
        var problem = await change.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        Assert.Contains(
            problem.GetProperty("errors").EnumerateArray(),
            reason => reason.GetString()!.Contains(AccountPolicy.MinPasswordLength.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Too_many_sign_ins_from_one_address_are_turned_away()
    {
        await using var app = new AggregatorApp();
        var browser = app.CreateBrowserClient();

        // Different names, so no account's lockout stops them first: guessing one password across many users.
        for (var i = 0; i < 20; i++)
        {
            await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, LoginAsync(browser, $"user{i}", "a common password guess"));
        }

        await HttpAssert.StatusAsync(
            HttpStatusCode.TooManyRequests, LoginAsync(browser, AggregatorApp.AdminUserName, AggregatorApp.AdminPassword));
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password, bool rememberMe = false) =>
        client.PostAsJsonAsync("/api/auth/login", new { userName, password, rememberMe }, Ct);

    private static string SessionCookie(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("cf_session=", StringComparison.Ordinal));

    private static async Task<string?> DetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("detail").GetString();
}
