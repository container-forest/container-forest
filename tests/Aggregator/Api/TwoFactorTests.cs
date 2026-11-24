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
            HttpStatusCode.OK,
            root.PutAsJsonAsync("/api/settings/sign-in", new { requireTwoFactorForAdministrators = true, requireTwoFactorAfterDiscord = false, confirmation }, Ct));

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

    [Fact]
    public async Task Required_for_administrators_a_password_needs_the_code_even_with_a_passkey()
    {
        await using var app = App();
        await app.CreateUserAsync("root", isAdmin: true);
        using var authenticator = new FakeAuthenticator();
        var root = await SignedInAsync(app, "root", origin: true);
        await AddPasskeyAsync(root, authenticator);
        await RequireAsync(root, administrators: true, discord: false);

        // A password alone is one factor, passkey or not; the limit lasts across requests (the session keeps its method).
        var password = await SignedInAsync(app, "root", origin: true);
        for (var request = 0; request < 3; request++)
        {
            await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, password.GetAsync("/api/environments", Ct));
        }

        var me = await password.GetFromJsonAsync<JsonElement>("/api/me", AggregatorApp.Json, Ct);
        Assert.True(me.GetProperty("twoFactorSetupRequired").GetBoolean());

        // The passkey is two factors.
        var passkey = Browser(app, origin: true);
        var options = await passkey.PostAsync("/api/auth/passkey/options", null, Ct);
        var credential = authenticator.Get(await options.Content.ReadAsStringAsync(Ct));
        var login = await passkey.PostAsJsonAsync("/api/auth/passkey/login", new { credential, rememberMe = false }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, login);
        Assert.False((await login.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("twoFactorSetupRequired").GetBoolean());
        await HttpAssert.StatusAsync(HttpStatusCode.OK, passkey.GetAsync("/api/environments", Ct));

        // With a passkey the app may be turned off: the passkey still signs in.
        var key = await EnableAsync(password);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, password.GetAsync("/api/environments", Ct));
        time.Advance();
        var status = await password.GetFromJsonAsync<JsonElement>("/api/me/two-factor", AggregatorApp.Json, Ct);
        Assert.False(status.GetProperty("required").GetBoolean());
        var confirmation = await ConfirmAsync(password, Code(key));
        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, password.PostAsJsonAsync("/api/me/two-factor/disable", new { confirmation }, Ct));
    }

    [Fact]
    public async Task Discord_does_not_ask_for_the_code_unless_the_setting_says_so()
    {
        await using var app = DiscordSignInTests.DiscordApp(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time)));
        await DiscordSignInTests.LinkWithCodeAsync(app, AggregatorApp.AdminUserName);
        var admin = await SignedInAsync(app, AggregatorApp.AdminUserName, AggregatorApp.AdminPassword);
        var key = await EnableAsync(admin, AggregatorApp.AdminPassword);
        time.Advance();

        // Off by default: Discord signs in on its own.
        var plain = Browser(app);
        Assert.Equal("/stacks", await DiscordSignInTests.DiscordRoundTripAsync(plain, "/api/auth/discord/login?redirect=%2Fstacks"));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, plain.GetAsync("/api/environments", Ct));

        await RequireAsync(admin, administrators: false, discord: true, AggregatorApp.AdminPassword, Code(key));
        time.Advance();

        var browser = Browser(app);
        var next = await DiscordSignInTests.DiscordRoundTripAsync(browser, "/api/auth/discord/login?redirect=%2Fstacks");
        Assert.Equal("/login?step=code&redirect=%2Fstacks", next);
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, browser.GetAsync("/api/me", Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, browser.PostAsJsonAsync("/api/auth/login/two-factor", new { code = Code(key) }, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.OK, browser.GetAsync("/api/environments", Ct));
        Assert.Contains("Discord and an authenticator app", await app.CreateClient().GetStringAsync("/api/audit", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discord_counted_as_a_password_limits_an_administrator_without_the_app()
    {
        await using var app = DiscordSignInTests.DiscordApp(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time)));
        await DiscordSignInTests.LinkWithCodeAsync(app, AggregatorApp.AdminUserName);
        // Through the dev header, which the requirement does not limit: a password session of this administrator would be.
        var admin = app.CreateClient();
        await RequireAsync(admin, administrators: true, discord: false, AggregatorApp.AdminPassword);

        var unlimited = Browser(app);
        await DiscordSignInTests.DiscordRoundTripAsync(unlimited, "/api/auth/discord/login");
        await HttpAssert.StatusAsync(HttpStatusCode.OK, unlimited.GetAsync("/api/environments", Ct));

        await RequireAsync(admin, administrators: true, discord: true, AggregatorApp.AdminPassword);
        var limited = Browser(app);
        await DiscordSignInTests.DiscordRoundTripAsync(limited, "/api/auth/discord/login");
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, limited.GetAsync("/api/environments", Ct));
    }

    private static async Task RequireAsync(HttpClient browser, bool administrators, bool discord, string password = Password, string? code = null)
    {
        var confirmation = await ConfirmAsync(browser, code, password);
        await HttpAssert.StatusAsync(
            HttpStatusCode.OK,
            browser.PutAsJsonAsync(
                "/api/settings/sign-in",
                new { requireTwoFactorForAdministrators = administrators, requireTwoFactorAfterDiscord = discord, confirmation },
                Ct));
    }

    private static async Task AddPasskeyAsync(HttpClient browser, FakeAuthenticator authenticator)
    {
        var options = await browser.PostAsync("/api/me/passkeys/options", null, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, options);
        var credential = authenticator.Create(await options.Content.ReadAsStringAsync(Ct));
        var added = await browser.PostAsJsonAsync("/api/me/passkeys", new { credential, name = "Laptop", confirmation = await ConfirmAsync(browser) }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, added);
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

    private async Task<string> EnableAsync(HttpClient browser, string password = Password)
    {
        var key = await BeginAsync(browser, password);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, browser.PostAsJsonAsync("/api/me/two-factor/enable", new { code = Code(key) }, Ct));
        return key;
    }

    private static async Task<string> BeginAsync(HttpClient browser, string password = Password)
    {
        var setup = await browser.PostAsJsonAsync("/api/me/two-factor/setup", new { confirmation = await ConfirmAsync(browser, null, password) }, Ct);
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

    private static async Task<string> ConfirmAsync(HttpClient browser, string? code = null, string password = Password)
    {
        var confirmed = await browser.PostAsJsonAsync("/api/auth/confirm/password", new { password, code }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, confirmed);
        return (await confirmed.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("token").GetString()!;
    }

    /// <param name="origin">With the Origin header that passkey requests need.</param>
    private static HttpClient Browser(AggregatorApp app, bool origin = false)
    {
        var browser = app.CreateBrowserClient();
        if (origin)
        {
            browser.DefaultRequestHeaders.Add("Origin", "http://localhost");
        }

        return browser;
    }

    private static async Task<HttpClient> SignedInAsync(AggregatorApp app, string userName, string password = Password, bool origin = false)
    {
        var browser = Browser(app, origin);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, LoginAsync(browser, userName, password));
        return browser;
    }

    private static async Task<HttpClient> SignedInAsync(AggregatorApp app, string userName, bool origin) =>
        await SignedInAsync(app, userName, Password, origin);

    private static Task<HttpResponseMessage> LoginAsync(HttpClient browser, string userName, string password = Password) =>
        browser.PostAsJsonAsync("/api/auth/login", new { userName, password, rememberMe = false }, Ct);

    /// <summary>The real clock, moved forward by the test: a code can be used once per 30-second step.</summary>
    private sealed class MovableTime : TimeProvider
    {
        private TimeSpan offset;

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;

        public void Advance(TimeSpan? by = null) => offset += by ?? TotpAuthenticator.Step;
    }
}
