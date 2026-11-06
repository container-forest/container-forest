using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>Passkeys (ADR-0009) end to end, with a software authenticator in place of the browser.</summary>
public class PasskeyTests
{
    private const string AlicePassword = "a passphrase of my own";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registered_passkey_signs_in_without_a_password()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        using var authenticator = new FakeAuthenticator();
        await RegisterAsync(await SignedInBrowserAsync(app, "alice"), authenticator, "Laptop");

        var browser = Browser(app);
        var login = await PasskeyLoginAsync(browser, authenticator);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, login);
        var me = await browser.GetFromJsonAsync<JsonElement>("/api/me", AggregatorApp.Json, Ct);
        Assert.Equal("alice", me.GetProperty("userName").GetString());
    }

    [Fact]
    public async Task Passkeys_are_listed_renamed_and_removed()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        using var authenticator = new FakeAuthenticator();
        var browser = await SignedInBrowserAsync(app, "alice");
        await RegisterAsync(browser, authenticator, "Laptop");

        var listed = await browser.GetFromJsonAsync<JsonElement[]>("/api/me/passkeys", AggregatorApp.Json, Ct);
        var passkey = Assert.Single(listed!);
        Assert.Equal(authenticator.CredentialId, passkey.GetProperty("id").GetString());
        Assert.Equal("Laptop", passkey.GetProperty("name").GetString());

        var renamed = await browser.PutAsJsonAsync($"/api/me/passkeys/{authenticator.CredentialId}", new { name = "Work laptop" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, renamed);
        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, browser.DeleteAsync($"/api/me/passkeys/{authenticator.CredentialId}", Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, PasskeyLoginAsync(Browser(app), authenticator));
    }

    [Fact]
    public async Task Passkey_of_a_deactivated_user_does_not_sign_in()
    {
        await using var app = new AggregatorApp();
        var alice = await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        using var authenticator = new FakeAuthenticator();
        await RegisterAsync(await SignedInBrowserAsync(app, "alice"), authenticator, "Laptop");
        await HttpAssert.StatusAsync(HttpStatusCode.OK, app.CreateClient().PostAsync($"/api/users/{alice}/deactivate", null, Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, PasskeyLoginAsync(Browser(app), authenticator));
    }

    [Fact]
    public async Task Unknown_passkey_does_not_sign_in()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        using var registered = new FakeAuthenticator();
        await RegisterAsync(await SignedInBrowserAsync(app, "alice"), registered, "Laptop");

        // A key made for alice's options but never registered: the aggregator has not seen it.
        using var stranger = new FakeAuthenticator();
        var options = await (await SignedInBrowserAsync(app, "alice")).PostAsync("/api/me/passkeys/options", null, Ct);
        stranger.Create(await options.Content.ReadAsStringAsync(Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, PasskeyLoginAsync(Browser(app), stranger));
    }

    [Fact]
    public async Task Answer_without_asking_for_options_is_refused()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        using var authenticator = new FakeAuthenticator();
        await RegisterAsync(await SignedInBrowserAsync(app, "alice"), authenticator, "Laptop");
        var options = await (await Browser(app).PostAsync("/api/auth/passkey/options", null, Ct)).Content.ReadAsStringAsync(Ct);

        // Another browser, without the challenge cookie of the first one.
        var login = await Browser(app).PostAsJsonAsync(
            "/api/auth/passkey/login", new { credential = authenticator.Get(options), rememberMe = false }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, login);
    }

    [Fact]
    public async Task Api_token_cannot_add_passkeys()
    {
        await using var app = new AggregatorApp();
        var token = await (await app.CreateClient().PostAsJsonAsync("/api/me/tokens", new { name = "cli", readOnly = false }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        var client = Browser(app);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("token").GetString());

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, client.PostAsync("/api/me/passkeys/options", null, Ct));
    }

    [Fact]
    public async Task Administrator_removes_a_users_lost_passkey()
    {
        await using var app = new AggregatorApp();
        var alice = await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        using var authenticator = new FakeAuthenticator();
        await RegisterAsync(await SignedInBrowserAsync(app, "alice"), authenticator, "Lost phone");
        var admin = app.CreateClient();

        Assert.Single((await admin.GetFromJsonAsync<JsonElement[]>($"/api/users/{alice}/passkeys", AggregatorApp.Json, Ct))!);
        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, admin.DeleteAsync($"/api/users/{alice}/passkeys/{authenticator.CredentialId}", Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, PasskeyLoginAsync(Browser(app), authenticator));
    }

    [Fact]
    public async Task Sign_in_methods_include_passkeys()
    {
        await using var app = new AggregatorApp();

        var methods = await app.CreateClientAs(null).GetFromJsonAsync<JsonElement>("/api/auth/methods", AggregatorApp.Json, Ct);

        Assert.True(methods.GetProperty("passkey").GetBoolean());
    }

    // The browser sends Origin with every fetch; Identity compares it with the origin in the credential.
    [Fact]
    public async Task A_session_alone_cannot_add_a_passkey()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        using var authenticator = new FakeAuthenticator();
        var browser = await SignedInBrowserAsync(app, "alice");

        var needs = await browser.GetFromJsonAsync<JsonElement>("/api/me/passkeys/needs-confirmation", AggregatorApp.Json, Ct);
        Assert.True(needs.GetProperty("required").GetBoolean());

        var options = await browser.PostAsync("/api/me/passkeys/options", null, Ct);
        var credential = authenticator.Create(await options.Content.ReadAsStringAsync(Ct));
        var added = await browser.PostAsJsonAsync("/api/me/passkeys", new { credential, name = "Stolen" }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, added);
        Assert.Empty((await browser.GetFromJsonAsync<JsonElement[]>("/api/me/passkeys", AggregatorApp.Json, Ct))!);
    }

    [Fact]
    public async Task An_account_with_nothing_to_confirm_with_adds_its_first_passkey_then_needs_a_confirmation()
    {
        await using var app = new AggregatorApp();
        var alice = await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        var browser = await SignedInBrowserAsync(app, "alice");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
            var account = await accounts.FindByIdAsync(alice.ToString());

            // Like an account that signs in only through Discord. Not RemovePasswordAsync: its new security stamp ends the session.
            account!.PasswordHash = null;
            Assert.True((await accounts.UpdateAsync(account)).Succeeded);
        }

        var needs = await browser.GetFromJsonAsync<JsonElement>("/api/me/passkeys/needs-confirmation", AggregatorApp.Json, Ct);
        Assert.False(needs.GetProperty("required").GetBoolean());

        using var first = new FakeAuthenticator();
        var options = await browser.PostAsync("/api/me/passkeys/options", null, Ct);
        var added = await browser.PostAsJsonAsync(
            "/api/me/passkeys", new { credential = first.Create(await options.Content.ReadAsStringAsync(Ct)), name = "First" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, added);

        needs = await browser.GetFromJsonAsync<JsonElement>("/api/me/passkeys/needs-confirmation", AggregatorApp.Json, Ct);
        Assert.True(needs.GetProperty("required").GetBoolean());
    }

    [Fact]
    public async Task A_passkey_confirms_the_identity_of_its_owner_only()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false, AlicePassword);
        await app.CreateUserAsync("bob", isAdmin: false, AlicePassword);
        using var authenticator = new FakeAuthenticator();
        var alice = await SignedInBrowserAsync(app, "alice");
        await RegisterAsync(alice, authenticator, "Laptop");

        var options = await alice.PostAsync("/api/auth/confirm/passkey/options", null, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, options);
        var credential = authenticator.Get(await options.Content.ReadAsStringAsync(Ct));
        var confirmed = await alice.PostAsJsonAsync("/api/auth/confirm/passkey", new { credential }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, confirmed);
        var body = await confirmed.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));

        // Bob signs in and answers his own challenge with Alice's passkey.
        var bob = await SignedInBrowserAsync(app, "bob");
        var bobOptions = await bob.PostAsync("/api/auth/passkey/options", null, Ct);
        var stolen = authenticator.Get(await bobOptions.Content.ReadAsStringAsync(Ct));
        var refused = await bob.PostAsJsonAsync("/api/auth/confirm/passkey", new { credential = stolen }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, refused);
    }

    private static HttpClient Browser(AggregatorApp app)
    {
        var browser = app.CreateBrowserClient();
        browser.DefaultRequestHeaders.Add("Origin", "http://localhost");
        return browser;
    }

    private static async Task<HttpClient> SignedInBrowserAsync(AggregatorApp app, string userName)
    {
        var browser = Browser(app);
        var login = await browser.PostAsJsonAsync("/api/auth/login", new { userName, password = AlicePassword, rememberMe = false }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, login);
        return browser;
    }

    private static async Task RegisterAsync(HttpClient browser, FakeAuthenticator authenticator, string name)
    {
        var options = await browser.PostAsync("/api/me/passkeys/options", null, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, options);
        var credential = authenticator.Create(await options.Content.ReadAsStringAsync(Ct));

        var confirmed = await browser.PostAsJsonAsync("/api/auth/confirm/password", new { password = AlicePassword }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, confirmed);
        var confirmation = (await confirmed.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("token").GetString();
        var added = await browser.PostAsJsonAsync("/api/me/passkeys", new { credential, name, confirmation }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, added);
    }

    private static async Task<HttpResponseMessage> PasskeyLoginAsync(HttpClient browser, FakeAuthenticator authenticator)
    {
        var options = await browser.PostAsync("/api/auth/passkey/options", null, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, options);
        var credential = authenticator.Get(await options.Content.ReadAsStringAsync(Ct));
        return await browser.PostAsJsonAsync("/api/auth/passkey/login", new { credential, rememberMe = false }, Ct);
    }
}
