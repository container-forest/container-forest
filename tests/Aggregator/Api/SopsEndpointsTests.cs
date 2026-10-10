using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ContainerForest.Aggregator.Api.Tests;

public class SopsEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Editor_encrypts_for_the_rule_and_decrypts_back()
    {
        await using var app = new AggregatorApp();
        var (alice, recipient) = await EditorWithKeyAsync(app, Role: "Editor");

        var encrypted = await ContentAsync(alice.PostAsJsonAsync("/api/sops/encrypt", Encrypt("secrets/.env", recipient, "TOKEN=s3cret\n"), Ct));
        Assert.Contains(recipient, Encoding.UTF8.GetString(encrypted), StringComparison.Ordinal);

        var plain = await ContentAsync(alice.PostAsJsonAsync("/api/sops/decrypt", new { path = "secrets/.env", content = encrypted }, Ct));
        Assert.Equal("TOKEN=s3cret", Encoding.UTF8.GetString(plain));
    }

    [Fact]
    public async Task Operator_encrypts_but_does_not_decrypt()
    {
        await using var app = new AggregatorApp();
        var (alice, recipient) = await EditorWithKeyAsync(app, Role: "Operator");

        var encrypted = await ContentAsync(alice.PostAsJsonAsync("/api/sops/encrypt", Encrypt(".env", recipient, "A=1\n"), Ct));

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, alice.PostAsJsonAsync("/api/sops/decrypt", new { path = ".env", content = encrypted }, Ct));
    }

    [Fact]
    public async Task Token_decrypts_only_with_the_right_to_decrypt_secrets()
    {
        await using var app = new AggregatorApp();
        var (alice, recipient) = await EditorWithKeyAsync(app, Role: "Editor");
        var encrypted = await ContentAsync(alice.PostAsJsonAsync("/api/sops/encrypt", Encrypt(".env", recipient, "A=1\n"), Ct));
        var body = new { path = ".env", content = encrypted };

        var plainToken = await CreateTokenAsync(alice, new { name = "ci", readOnly = false });
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, WithToken(app, plainToken).PostAsJsonAsync("/api/sops/decrypt", body, Ct));

        var secretsToken = await CreateTokenAsync(alice, new { name = "git", readOnly = false, secrets = true });
        await HttpAssert.StatusAsync(HttpStatusCode.OK, WithToken(app, secretsToken).PostAsJsonAsync("/api/sops/decrypt", body, Ct));
        var listed = await alice.GetFromJsonAsync<JsonElement>("/api/me/tokens", AggregatorApp.Json, Ct);
        Assert.Equal([true, false], listed.EnumerateArray().Select(t => t.GetProperty("secrets").GetBoolean()));

        var readOnly = await alice.PostAsJsonAsync("/api/me/tokens", new { name = "ro", readOnly = true, secrets = true }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, readOnly);
        var readOnlyToken = await CreateTokenAsync(alice, new { name = "ro", readOnly = true });
        await HttpAssert.StatusAsync(
            HttpStatusCode.Forbidden, WithToken(app, readOnlyToken).PostAsJsonAsync("/api/sops/encrypt", Encrypt(".env", recipient, "A=1\n"), Ct));
    }

    [Fact]
    public async Task Unusable_requests_are_explained()
    {
        await using var app = new AggregatorApp();
        var (alice, recipient) = await EditorWithKeyAsync(app, Role: "Editor");

        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, alice.PostAsJsonAsync(
            "/api/sops/encrypt", new { path = "a.env", sopsConfig = "creation_rules:\n  - kms: arn\n", content = "A=1"u8.ToArray() }, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, alice.PostAsJsonAsync(
            "/api/sops/decrypt", new { path = "../a.env", content = "A=1"u8.ToArray() }, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, alice.PostAsJsonAsync(
            "/api/sops/decrypt", new { path = "a.env", content = Encoding.UTF8.GetBytes($"A={recipient}\n") }, Ct));
        var foreign = "encrypted for x: A\nsops_age__list_0__map_recipient=age1lggyhqrw2nlhcxprm67z43rta597azn8gknawjehu9d9dl0jq3yqqvfafg\nsops_version=3.13.3\n";
        await HttpAssert.StatusAsync(HttpStatusCode.UnprocessableEntity, alice.PostAsJsonAsync(
            "/api/sops/decrypt", new { path = "a.env", content = Encoding.UTF8.GetBytes(foreign) }, Ct));
    }

    [Fact]
    public async Task Cli_login_asks_for_the_right_and_the_person_grants_it()
    {
        await using var app = new AggregatorApp();
        var anonymous = app.CreateClientAs(null);
        var started = await (await anonymous.PostAsJsonAsync(
            "/api/cli/authorizations", new { clientName = "forest", hostName = "laptop", secrets = true }, Ct)).Content
            .ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        var userCode = started.GetProperty("userCode").GetString();
        var admin = app.CreateClient();

        var request = await admin.GetFromJsonAsync<JsonElement>($"/api/cli/authorizations/{userCode}", AggregatorApp.Json, Ct);
        Assert.True(request.GetProperty("secretsRequested").GetBoolean());
        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, admin.PostAsJsonAsync(
            $"/api/cli/authorizations/{userCode}/approve", new { name = "forest", readOnly = false, secrets = true }, Ct));

        var token = await (await anonymous.PostAsJsonAsync(
            "/api/cli/token", new { deviceCode = started.GetProperty("deviceCode").GetString() }, Ct)).Content
            .ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        Assert.True(token.GetProperty("apiToken").GetProperty("secrets").GetBoolean());
    }

    // An environment with an age key, and alice with the given role on it.
    private static async Task<(HttpClient Alice, string Recipient)> EditorWithKeyAsync(AggregatorApp app, string Role)
    {
        var admin = app.CreateClient();
        var aliceId = await app.CreateUserAsync("alice", isAdmin: false);
        var environment = await app.CreateEnvironmentAsync("prod");
        var key = await admin.PostAsync($"/api/environments/{environment.Id}/age-keys", null, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, key);
        var recipient = (await key.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("recipient").GetString()!;
        await HttpAssert.StatusAsync(HttpStatusCode.Created, admin.PostAsJsonAsync(
            $"/api/users/{aliceId}/role-assignments", new { environmentId = environment.Id, stackId = (Guid?)null, role = Role }, Ct));
        return (app.CreateClientAs("alice"), recipient);
    }

    private static object Encrypt(string path, string recipient, string content) => new
    {
        path,
        sopsConfig = $"creation_rules:\n  - age: {recipient}\n",
        content = Encoding.UTF8.GetBytes(content),
    };

    private static async Task<byte[]> ContentAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("content").GetBytesFromBase64();
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
