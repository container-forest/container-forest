using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using AspNet.Security.OAuth.Discord;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>Signing in with Discord and linking through Discord's OAuth2 (ADR-0009), with Discord's API faked.</summary>
public class DiscordSignInTests
{
    private const ulong DiscordId = 123456789012345678;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Linked_discord_account_signs_in()
    {
        await using var app = DiscordApp();
        await LinkWithCodeAsync(app, AggregatorApp.AdminUserName);
        var browser = app.CreateBrowserClient();

        var next = await DiscordRoundTripAsync(browser, "/api/auth/discord/login?redirect=%2Fstacks");

        Assert.Equal("/stacks", next);
        var me = await browser.GetFromJsonAsync<JsonElement>("/api/me", AggregatorApp.Json, Ct);
        Assert.Equal(AggregatorApp.AdminUserName, me.GetProperty("userName").GetString());
    }

    [Fact]
    public async Task Unlinked_discord_account_does_not_sign_in()
    {
        await using var app = DiscordApp();
        var browser = app.CreateBrowserClient();

        var next = await DiscordRoundTripAsync(browser, "/api/auth/discord/login");

        Assert.Equal("/login?error=discord", next);
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, browser.GetAsync("/api/me", Ct));
    }

    [Fact]
    public async Task Signed_in_user_links_their_discord_account()
    {
        await using var app = DiscordApp();
        var browser = app.CreateBrowserClient();
        var login = await browser.PostAsJsonAsync(
            "/api/auth/login", new { userName = AggregatorApp.AdminUserName, password = AggregatorApp.AdminPassword, rememberMe = false }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, login);

        var next = await DiscordRoundTripAsync(browser, "/api/me/discord/oauth");

        Assert.Equal("/discord?linked=discord", next);
        var link = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/me/discord", AggregatorApp.Json, Ct);
        Assert.True(link.GetProperty("linked").GetBoolean());
        Assert.Equal("alice_d", link.GetProperty("discordUserName").GetString());
    }

    [Fact]
    public async Task Linking_without_a_session_goes_to_the_login_page()
    {
        await using var app = DiscordApp();

        var response = await app.CreateBrowserClient().GetAsync("/api/me/discord/oauth", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?redirect=%2Fdiscord", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Redirect_to_another_site_is_ignored()
    {
        await using var app = DiscordApp();
        await LinkWithCodeAsync(app, AggregatorApp.AdminUserName);

        var next = await DiscordRoundTripAsync(app.CreateBrowserClient(), "/api/auth/discord/login?redirect=%2F%2Fevil.example");

        Assert.Equal("/", next);
    }

    [Fact]
    public async Task Callback_from_another_browser_is_refused()
    {
        await using var app = DiscordApp();
        await LinkWithCodeAsync(app, AggregatorApp.AdminUserName);
        var challenge = await app.CreateBrowserClient().GetAsync("/api/auth/discord/login", Ct);
        var state = HttpUtility.ParseQueryString(challenge.Headers.Location!.Query)["state"];

        // The victim's browser lacks the correlation cookie the attacker's challenge set.
        var victim = app.CreateBrowserClient();
        var callback = await victim.GetAsync($"/api/auth/discord/callback?code=code&state={Uri.EscapeDataString(state!)}", Ct);

        Assert.Equal("/login?error=discord", callback.Headers.Location!.OriginalString);
        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, victim.GetAsync("/api/me", Ct));
    }

    [Fact]
    public async Task Without_configuration_discord_sign_in_is_off()
    {
        await using var app = new AggregatorApp();

        var methods = await app.CreateClientAs(null).GetFromJsonAsync<JsonElement>("/api/auth/methods", AggregatorApp.Json, Ct);
        var login = await app.CreateBrowserClient().GetAsync("/api/auth/discord/login", Ct);

        Assert.False(methods.GetProperty("discord").GetBoolean());
        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, login);
    }

    /// <param name="services">More test services, e.g. a clock for two-factor tests.</param>
    internal static AggregatorApp DiscordApp(Action<IServiceCollection>? services = null) => new()
    {
        Customize = builder =>
        {
            builder.UseSetting("DiscordAuth:ClientId", "client-id");
            builder.UseSetting("DiscordAuth:ClientSecret", "client-secret");
            builder.ConfigureTestServices(collection =>
            {
                collection.Configure<DiscordAuthenticationOptions>(
                    DiscordAuthenticationDefaults.AuthenticationScheme, options => options.BackchannelHttpHandler = new FakeDiscord());
                services?.Invoke(collection);
            });
        },
    };

    /// <summary>
    /// Starts at <paramref name="start"/>, plays Discord (approves at once), follows the aggregator's redirects and
    /// returns where its continue page sends the browser.
    /// </summary>
    internal static async Task<string> DiscordRoundTripAsync(HttpClient browser, string start)
    {
        var challenge = await browser.GetAsync(start, Ct);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var authorize = challenge.Headers.Location!;
        Assert.Equal("discord.com", authorize.Host);
        var query = HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal("identify", query["scope"]);

        var callback = await browser.GetAsync($"/api/auth/discord/callback?code=code&state={Uri.EscapeDataString(query["state"]!)}", Ct);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        var page = await browser.GetAsync(callback.Headers.Location!, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, page);
        var html = await page.Content.ReadAsStringAsync(Ct);
        var start_ = html.IndexOf("url=", StringComparison.Ordinal) + 4;
        return WebUtility.HtmlDecode(html[start_..html.IndexOf('"', start_)]);
    }

    internal static async Task LinkWithCodeAsync(AggregatorApp app, string userName)
    {
        var code = await (await app.CreateClientAs(userName).PostAsync("/api/me/discord/link-code", null, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        var linked = await app.BotGateway.LinkAccountAsync(
            new global::ContainerForest.Contracts.Bot.LinkAccountRequest { DiscordUserId = DiscordId, DiscordUserName = "alice_d", Code = code.GetProperty("code").GetString() },
            BotHeaders(), cancellationToken: Ct);
        Assert.NotNull(linked);
    }

    private static global::Grpc.Core.Metadata BotHeaders() => new() { { "x-bot-token", AggregatorApp.BotToken } };

    /// <summary>Discord's token and user endpoints: any code is good, and the user is always the same.</summary>
    private sealed class FakeDiscord : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/api/oauth2/token" => """{"access_token":"discord-token","token_type":"Bearer","expires_in":3600}""",
                "/api/users/@me" => $$"""{"id":"{{DiscordId}}","username":"alice_d","global_name":"Alice"}""",
                _ => null,
            };
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
