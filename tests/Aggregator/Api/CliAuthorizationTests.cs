using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ContainerForest.Aggregator.Api.Tests;

public class CliAuthorizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cli_gets_a_token_after_the_user_approves_in_the_panel()
    {
        await using var app = new AggregatorApp();
        var cli = app.CreateClientAs(null);
        var started = await StartAsync(cli);
        var userCode = started.GetProperty("userCode").GetString()!;
        var deviceCode = started.GetProperty("deviceCode").GetString()!;
        Assert.Equal($"/cli/authorize?code={userCode}", started.GetProperty("verificationPathComplete").GetString());

        Assert.Equal("Pending", await PollStatusAsync(cli, deviceCode));
        var panel = app.CreateClient();
        var request = await panel.GetFromJsonAsync<JsonElement>($"/api/cli/authorizations/{userCode.ToLowerInvariant()}", AggregatorApp.Json, Ct);
        Assert.Equal("my-laptop", request.GetProperty("hostName").GetString());
        var approve = await panel.PostAsJsonAsync(
            $"/api/cli/authorizations/{userCode}/approve",
            new { name = "forest on my-laptop", readOnly = true, environmentIds = (Guid[]?)null, expiresAt = (DateTimeOffset?)null },
            Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, approve);

        var token = await PollAsync(cli, deviceCode, until: "Approved");
        Assert.Equal(AggregatorApp.AdminUserName, token.GetProperty("userName").GetString());
        Assert.True(token.GetProperty("apiToken").GetProperty("readOnly").GetBoolean());

        var withToken = app.CreateClientAs(null);
        withToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("token").GetString());
        await HttpAssert.StatusAsync(HttpStatusCode.OK, withToken.GetAsync("/api/me", Ct));
        Assert.Equal("Expired", await PollStatusAsync(cli, deviceCode));
    }

    [Fact]
    public async Task Approval_needs_a_session()
    {
        await using var app = new AggregatorApp();
        var started = await StartAsync(app.CreateClientAs(null));
        var userCode = started.GetProperty("userCode").GetString()!;

        var anonymous = await app.CreateClientAs(null).PostAsJsonAsync(
            $"/api/cli/authorizations/{userCode}/approve", new { name = "x", readOnly = false }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Unauthorized, anonymous);
    }

    [Fact]
    public async Task Denied_login_tells_the_cli()
    {
        await using var app = new AggregatorApp();
        var cli = app.CreateClientAs(null);
        var started = await StartAsync(cli);

        await HttpAssert.StatusAsync(
            HttpStatusCode.NoContent,
            app.CreateClient().PostAsync($"/api/cli/authorizations/{started.GetProperty("userCode").GetString()}/deny", null, Ct));

        Assert.Equal("Denied", await PollStatusAsync(cli, started.GetProperty("deviceCode").GetString()!));
    }

    [Fact]
    public async Task Unknown_user_code_is_not_found()
    {
        await using var app = new AggregatorApp();

        await HttpAssert.StatusAsync(HttpStatusCode.NotFound, app.CreateClient().GetAsync("/api/cli/authorizations/ABCD-EFGH", Ct));
    }

    [Fact]
    public async Task Starting_too_often_is_rate_limited()
    {
        await using var app = new AggregatorApp();
        var cli = app.CreateClientAs(null);

        HttpResponseMessage? last = null;
        for (var i = 0; i < 11; i++)
        {
            last = await cli.PostAsJsonAsync("/api/cli/authorizations", new { clientName = "forest", hostName = "h" }, Ct);
        }

        await HttpAssert.StatusAsync(HttpStatusCode.TooManyRequests, last!);
    }

    private static async Task<JsonElement> StartAsync(HttpClient cli)
    {
        var response = await cli.PostAsJsonAsync("/api/cli/authorizations", new { clientName = "forest 0.1", hostName = "my-laptop" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        return await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
    }

    private static async Task<string> PollStatusAsync(HttpClient cli, string deviceCode) =>
        (await PollOnceAsync(cli, deviceCode)).GetProperty("status").GetString()!;

    private static async Task<JsonElement> PollOnceAsync(HttpClient cli, string deviceCode)
    {
        var response = await cli.PostAsJsonAsync("/api/cli/token", new { deviceCode }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        return await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
    }

    // The aggregator asks to slow down when polled within the interval; the test waits instead of polling faster.
    private static async Task<JsonElement> PollAsync(HttpClient cli, string deviceCode, string until)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await PollOnceAsync(cli, deviceCode);
            var status = response.GetProperty("status").GetString();
            if (status == until)
            {
                return response;
            }

            Assert.Equal("SlowDown", status);
            await Task.Delay(Domain.Access.CliAuthorization.PollInterval, Ct);
        }

        throw new TimeoutException($"The CLI login did not become {until}.");
    }
}
