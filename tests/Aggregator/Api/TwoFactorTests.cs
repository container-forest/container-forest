using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>Two-factor authentication with an authenticator app (ADR-0019), end to end.</summary>
public class TwoFactorTests
{
    private const string Password = AggregatorApp.DefaultPassword;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MovableTime time = new();

    [Fact]
    public async Task Password_sign_in_asks_for_the_code_of_the_app_set_up()
    {
        await using var app = App();
        await app.CreateUserAsync("alice", isAdmin: false);
        var key = await EnableAsync(app, "alice");
        time.Advance();

        var browser = Browser(app);
        var login = await LoginAsync(browser, "alice");
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, login);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("cf_2fa=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, browser.GetAsync("/api/me", Ct));

        var completed = await browser.PostAsJsonAsync("/api/auth/login/two-factor", new { code = Code(key) }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, completed);
        var me = await browser.GetFromJsonAsync<JsonElement>("/api/me", AggregatorApp.Json, Ct);
        Assert.Equal(("alice", true), (me.GetProperty("userName").GetString(), me.GetProperty("twoFactorEnabled").GetBoolean()));
    }

    [Fact]
    public async Task A_code_works_once_and_a_wrong_one_is_refused()
    {
        await using var app = App();
        await app.CreateUserAsync("alice", isAdmin: false);
        var key = await EnableAsync(app, "alice");

        // The code that turned two-factor on was of this step: it is spent.
        var browser = Browser(app);
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, LoginAsync(browser, "alice"));
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, browser.PostAsJsonAsync("/api/auth/login/two-factor", new { code = Code(key) }, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, browser.PostAsJsonAsync("/api/auth/login/two-factor", new { code = "000000" }, Ct));

        time.Advance();
        var code = Code(key);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, browser.PostAsJsonAsync("/api/auth/login/two-factor", new { code }, Ct));

        var again = Browser(app);
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, LoginAsync(again, "alice"));
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, again.PostAsJsonAsync("/api/auth/login/two-factor", new { code }, Ct));
    }

    [Fact]
    public async Task Code_without_the_password_step_signs_nobody_in()
    {
        await using var app = App();
        await app.CreateUserAsync("alice", isAdmin: false);
        var key = await EnableAsync(app, "alice");
        time.Advance();

        await HttpAssert.StatusAsync(
            HttpStatusCode.Unauthorized, Browser(app).PostAsJsonAsync("/api/auth/login/two-factor", new { code = Code(key) }, Ct));

        // The password step expires.
        var browser = Browser(app);
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, LoginAsync(browser, "alice"));
        time.Advance(TimeSpan.FromMinutes(6));
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, browser.PostAsJsonAsync("/api/auth/login/two-factor", new { code = Code(key) }, Ct));
    }

    [Fact]
    public async Task Recovery_code_signs_in_once()
    {
        await using var app = App();
        await app.CreateUserAsync("alice", isAdmin: false);
        var (_, codes) = await EnableWithCodesAsync(app, "alice");
        Assert.Equal(10, codes.Count);

        var browser = Browser(app);
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, LoginAsync(browser, "alice"));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, browser.PostAsJsonAsync("/api/auth/login/two-factor", new { code = codes[0] }, Ct));
        var status = await browser.GetFromJsonAsync<JsonElement>("/api/me/two-factor", AggregatorApp.Json, Ct);
        Assert.Equal(9, status.GetProperty("recoveryCodesLeft").GetInt32());

        var again = Browser(app);
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, LoginAsync(again, "alice"));
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, again.PostAsJsonAsync("/api/auth/login/two-factor", new { code = codes[0] }, Ct));
    }

    [Fact]
    public async Task Wrong_codes_lock_the_account_like_wrong_passwords()
    {
        await using var app = App();
        await app.CreateUserAsync("alice", isAdmin: false);
        var key = await EnableAsync(app, "alice");
        time.Advance();

        var browser = Browser(app);
        await HttpAssert.StatusAsync(HttpStatusCode.Accepted, LoginAsync(browser, "alice"));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, browser.PostAsJsonAsync("/api/auth/login/two-factor", new { code = "000000" }, Ct));
        }

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, browser.PostAsJsonAsync("/api/auth/login/two-factor", new { code = Code(key) }, Ct));
    }

    [Fact]
    public async Task Key_and_recovery_codes_are_not_stored_in_clear()
    {
        await using var app = App();
        await app.CreateUserAsync("alice", isAdmin: false);
        var (key, codes) = await EnableWithCodesAsync(app, "alice");

        Assert.False(await app.DatabaseContainsAsync(key));
        Assert.False(await app.DatabaseContainsAsync(codes[0]));
    }

    [Fact]
    public async Task Setup_and_turning_off_need_a_fresh_confirmation_with_the_code()
    {
        await using var app = App();
        await app.CreateUserAsync("alice", isAdmin: false);
        var browser = await SignedInAsync(app, "alice");
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, browser.PostAsJsonAsync("/api/me/two-factor/setup", new { confirmation = (string?)null }, Ct));
        var key = await EnableAsync(browser);
        time.Advance();

        // With two-factor on, a password alone confirms nothing.
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, browser.PostAsJsonAsync("/api/auth/confirm/password", new { password = Password }, Ct));
        var confirmation = await ConfirmAsync(browser, Code(key));
        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, browser.PostAsJsonAsync("/api/me/two-factor/disable", new { confirmation }, Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(Browser(app), "alice"));
    }

    [Fact]
    public async Task Administrator_resets_a_lost_app_and_sees_who_has_two_factor()
    {
        await using var app = App();
        var alice = await app.CreateUserAsync("alice", isAdmin: false);
        await EnableAsync(app, "alice");
        var admin = app.CreateClient();

        var users = await admin.GetFromJsonAsync<JsonElement[]>("/api/users", AggregatorApp.Json, Ct);
        Assert.True(users!.Single(u => u.GetProperty("userName").GetString() == "alice").GetProperty("twoFactorEnabled").GetBoolean());
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, app.CreateClientAs("alice").DeleteAsync($"/api/users/{alice}/two-factor", Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, admin.DeleteAsync($"/api/users/{alice}/two-factor", Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(Browser(app), "alice"));
        var audit = await admin.GetStringAsync("/api/audit", Ct);
        Assert.Contains("TwoFactorReset", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Required_for_administrators_limits_their_sessions_to_setting_it_up()
    {
        await using var app = App();
        await app.CreateUserAsync("root", isAdmin: true);
        await app.CreateUserAsync("alice", isAdmin: false);
        var root = await SignedInAsync(app, "root");
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, root.PutAsJsonAsync("/api/settings/sign-in", new { requireTwoFactorForAdministrators = true }, Ct));
        var confirmation = await ConfirmAsync(root);
        await HttpAssert.StatusAsync(
            HttpStatusCode.OK, root.PutAsJsonAsync("/api/settings/sign-in", new { requireTwoFactorForAdministrators = true, confirmation }, Ct));

        var blocked = await root.GetAsync("/api/environments", Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, blocked);
        Assert.Contains("TwoFactorSetupRequired", await blocked.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var me = await root.GetFromJsonAsync<JsonElement>("/api/me", AggregatorApp.Json, Ct);
        Assert.True(me.GetProperty("twoFactorSetupRequired").GetBoolean());

        // Not an administrator, an API token, or the dev header: nothing changes.
        await HttpAssert.StatusAsync(HttpStatusCode.OK, (await SignedInAsync(app, "alice")).GetAsync("/api/environments", Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, app.CreateClientAs("root").GetAsync("/api/environments", Ct));

        await EnableAsync(root);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, root.GetAsync("/api/environments", Ct));
        time.Advance();
        var disable = await root.PostAsJsonAsync("/api/me/two-factor/disable", new { confirmation = await ConfirmAsync(root, Code(await KeyAsync(app, "root"))) }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, disable);
    }

    private AggregatorApp App() => new()
    {
        Customize = builder => builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time))),
    };

    private string Code(string key) => TotpAuthenticator.Compute(Base32.Decode(key)!, TotpAuthenticator.StepAt(time.GetUtcNow()));

    private async Task<string> EnableAsync(AggregatorApp app, string userName) => (await EnableWithCodesAsync(app, userName)).Key;

    private async Task<(string Key, IReadOnlyList<string> Codes)> EnableWithCodesAsync(AggregatorApp app, string userName)
    {
        var browser = await SignedInAsync(app, userName);
        var key = await BeginAsync(browser);
        var enabled = await browser.PostAsJsonAsync("/api/me/two-factor/enable", new { code = Code(key) }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, enabled);
        var codes = (await enabled.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("codes")
            .EnumerateArray().Select(c => c.GetString()!).ToList();
        return (key, codes);
    }

    private async Task<string> EnableAsync(HttpClient browser)
    {
        var key = await BeginAsync(browser);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, browser.PostAsJsonAsync("/api/me/two-factor/enable", new { code = Code(key) }, Ct));
        return key;
    }

    private static async Task<string> BeginAsync(HttpClient browser)
    {
        var setup = await browser.PostAsJsonAsync("/api/me/two-factor/setup", new { confirmation = await ConfirmAsync(browser) }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, setup);
        var body = await setup.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        Assert.StartsWith("otpauth://totp/container-forest:", body.GetProperty("uri").GetString(), StringComparison.Ordinal);
        return body.GetProperty("sharedKey").GetString()!;
    }

    /// <summary>The key as the aggregator holds it, for a test that set it up in another browser.</summary>
    private static async Task<string> KeyAsync(AggregatorApp app, string userName)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var accounts = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AccountUser>>();
        return (await accounts.GetAuthenticatorKeyAsync((await accounts.FindByNameAsync(userName))!))!;
    }

    private static async Task<string> ConfirmAsync(HttpClient browser, string? code = null)
    {
        var confirmed = await browser.PostAsJsonAsync("/api/auth/confirm/password", new { password = Password, code }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, confirmed);
        return (await confirmed.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("token").GetString()!;
    }

    private static HttpClient Browser(AggregatorApp app) => app.CreateBrowserClient();

    private static async Task<HttpClient> SignedInAsync(AggregatorApp app, string userName)
    {
        var browser = Browser(app);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(browser, userName));
        return browser;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient browser, string userName) =>
        browser.PostAsJsonAsync("/api/auth/login", new { userName, password = Password, rememberMe = false }, Ct);

    /// <summary>The real clock, moved forward by the test: a code can be used once per 30-second step.</summary>
    private sealed class MovableTime : TimeProvider
    {
        private TimeSpan offset;

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;

        public void Advance(TimeSpan? by = null) => offset += by ?? TotpAuthenticator.Step;
    }
}
