using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContainerForest.Aggregator.Application.Backups;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ContainerForest.Aggregator.Api.Tests;

/// <summary>The settings store and the backup through the API (ADR-0013, ADR-0014), with the archive and the bucket in memory.</summary>
public class BackupTests
{
    private const string Recipient = "age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Settings(string? confirmation, bool enabled = true, string? secret = "the-secret-key", string bucket = "forest") => new
    {
        enabled,
        intervalHours = 24,
        recipient = Recipient,
        endpoint = "https://account.r2.example.com",
        bucket,
        region = "",
        prefix = "forest/",
        accessKeyId = "key-id",
        secretAccessKey = secret,
        confirmation,
    };

    [Fact]
    public async Task Settings_start_off_with_nothing_set()
    {
        await using var app = NewApp(out _);

        var settings = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/settings/backup", AggregatorApp.Json, Ct);

        Assert.Equal("false", settings.GetProperty("enabled").GetProperty("value").GetString());
        Assert.Equal("Default", settings.GetProperty("enabled").GetProperty("source").GetString());
        Assert.False(settings.GetProperty("secretAccessKey").GetProperty("isSet").GetBoolean());
    }

    [Fact]
    public async Task Saving_needs_a_fresh_confirmation()
    {
        await using var app = NewApp(out _);
        var browser = await SignedInAdminAsync(app);

        var without = await browser.PutAsJsonAsync("/api/settings/backup", Settings(confirmation: null), Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, without);
        var forged = await browser.PutAsJsonAsync("/api/settings/backup", Settings("not-a-token"), Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, forged);

        var saved = await browser.PutAsJsonAsync("/api/settings/backup", Settings(await ConfirmAsync(browser)), Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, saved);
    }

    [Fact]
    public async Task A_wrong_password_gives_no_confirmation()
    {
        await using var app = NewApp(out _);
        var browser = await SignedInAdminAsync(app);

        var response = await browser.PostAsJsonAsync("/api/auth/confirm/password", new { password = "wrong" }, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task A_confirmation_is_only_for_the_user_it_was_given_to()
    {
        await using var app = NewApp(out _);
        await app.CreateUserAsync("second", isAdmin: true);
        var token = await ConfirmAsync(await SignedInAdminAsync(app));

        var second = await SignedInAsync(app, "second", AggregatorApp.DefaultPassword);
        var response = await second.PutAsJsonAsync("/api/settings/backup", Settings(token), Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task Other_users_can_neither_read_nor_change_settings()
    {
        await using var app = NewApp(out _);
        await app.CreateUserAsync("alice", isAdmin: false);
        var alice = app.CreateClientAs("alice");

        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, alice.GetAsync("/api/settings/backup", Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, alice.GetAsync("/api/backup", Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, alice.PostAsync("/api/backup/run", null, Ct));
    }

    [Fact]
    public async Task The_secret_never_comes_back_and_is_not_stored_in_clear()
    {
        await using var app = NewApp(out _);
        var browser = await SignedInAdminAsync(app);

        var saved = await browser.PutAsJsonAsync("/api/settings/backup", Settings(await ConfirmAsync(browser)), Ct);

        var body = await saved.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("the-secret-key", body);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("secretAccessKey").GetProperty("isSet").GetBoolean());
        Assert.False(await app.DatabaseContainsAsync("the-secret-key"));
    }

    [Fact]
    public async Task A_null_secret_keeps_the_stored_one_and_an_empty_one_removes_it()
    {
        await using var app = NewApp(out _);
        var browser = await SignedInAdminAsync(app);
        await SaveAsync(browser, Settings(await ConfirmAsync(browser)));

        var kept = await SaveAsync(browser, Settings(await ConfirmAsync(browser), secret: null));
        Assert.True(kept.GetProperty("secretAccessKey").GetProperty("isSet").GetBoolean());

        var removed = await SaveAsync(browser, Settings(await ConfirmAsync(browser), enabled: false, secret: ""));
        Assert.False(removed.GetProperty("secretAccessKey").GetProperty("isSet").GetBoolean());
    }

    [Fact]
    public async Task Backups_cannot_be_turned_on_with_settings_missing()
    {
        await using var app = NewApp(out _);
        var browser = await SignedInAdminAsync(app);

        var response = await browser.PutAsJsonAsync(
            "/api/settings/backup", Settings(await ConfirmAsync(browser), secret: null), Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, response);
    }

    [Theory]
    [InlineData("http://storage.example.com")]
    [InlineData("https://storage.example.com/path")]
    [InlineData("https://user:password@storage.example.com")]
    public async Task An_unusable_endpoint_is_refused(string endpoint)
    {
        await using var app = NewApp(out _);
        var browser = await SignedInAdminAsync(app);

        var response = await browser.PutAsJsonAsync(
            "/api/settings/backup",
            new { enabled = false, intervalHours = 24, endpoint, confirmation = await ConfirmAsync(browser) },
            Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.BadRequest, response);
    }

    [Fact]
    public async Task The_environment_wins_and_locks_the_field()
    {
        await using var app = NewApp(out _, builder =>
        {
            builder.UseSetting("Backup:Bucket", "from-environment");
            builder.UseSetting("Backup:Enabled", "false");
        });
        var browser = await SignedInAdminAsync(app);

        var saved = await SaveAsync(browser, Settings(await ConfirmAsync(browser), bucket: "from-panel"));

        var bucket = saved.GetProperty("bucket");
        Assert.Equal("from-environment", bucket.GetProperty("value").GetString());
        Assert.Equal("Environment", bucket.GetProperty("source").GetString());
        Assert.True(bucket.GetProperty("locked").GetBoolean());
        Assert.Equal("false", saved.GetProperty("enabled").GetProperty("value").GetString());
    }

    [Fact]
    public async Task An_invalid_value_in_the_environment_is_reported_and_stops_no_start()
    {
        await using var app = NewApp(out _, builder => builder.UseSetting("Backup:Endpoint", "ftp://nope"));

        var settings = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/settings/backup", AggregatorApp.Json, Ct);
        var status = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/backup", AggregatorApp.Json, Ct);

        Assert.NotNull(settings.GetProperty("endpoint").GetProperty("problem").GetString());
        Assert.Contains(status.GetProperty("problems").EnumerateArray(), p => p.GetString()!.StartsWith("Endpoint:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_change_is_audited_by_field_name_without_values()
    {
        await using var app = NewApp(out _);
        var browser = await SignedInAdminAsync(app);

        await SaveAsync(browser, Settings(await ConfirmAsync(browser)));

        var audit = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/audit?category=System", AggregatorApp.Json, Ct);
        var entry = audit.GetProperty("entries").EnumerateArray().First(e => e.GetProperty("action").GetString() == "SettingsChanged");
        var details = entry.GetProperty("details").GetString()!;
        Assert.Contains("Recipient", details);
        Assert.Contains("SecretAccessKey", details);
        Assert.DoesNotContain("the-secret-key", audit.GetRawText());
        Assert.DoesNotContain(Recipient, audit.GetRawText());
    }

    [Fact]
    public async Task A_change_is_announced_to_a_rule_on_the_whole_system_but_not_on_an_environment()
    {
        await using var app = NewApp(out _);
        var environment = await app.CreateEnvironmentAsync();
        var admin = app.CreateClient();
        var channel = await CreateChannelAsync(admin, "system", "111");
        await CreateRuleAsync(admin, channel, "SettingsChanged", null);
        var other = await CreateChannelAsync(admin, "environment", "222");
        await CreateRuleAsync(admin, other, "SettingsChanged", environment.Id);
        var browser = await SignedInAdminAsync(app);

        await SaveAsync(browser, Settings(await ConfirmAsync(browser)));

        var history = await admin.GetFromJsonAsync<JsonElement[]>("/api/notifications", AggregatorApp.Json, Ct);
        var notification = Assert.Single(history!);
        Assert.Equal("SettingsChanged", notification.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, notification.GetProperty("environmentId").ValueKind);
        Assert.Contains("admin", notification.GetProperty("summary").GetString());
        Assert.Equal(channel, notification.GetProperty("channelId").GetGuid());
        Assert.DoesNotContain("the-secret-key", notification.GetRawText());
    }

    [Fact]
    public async Task A_run_uploads_the_archive_and_is_recorded()
    {
        await using var app = NewApp(out var storage);
        var browser = await SignedInAdminAsync(app);
        await SaveAsync(browser, Settings(await ConfirmAsync(browser), enabled: false));

        var response = await app.CreateClient().PostAsync("/api/backup/run", null, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        var run = await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        Assert.Equal("Succeeded", run.GetProperty("outcome").GetString());
        var upload = Assert.Single(storage.Uploads);
        Assert.StartsWith("forest/forest-", upload.Key);
        Assert.EndsWith(".tar.gz.age", upload.Key);
        Assert.Equal("the-secret-key", upload.Destination.SecretAccessKey);

        var status = await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/backup", AggregatorApp.Json, Ct);
        Assert.Equal(upload.Key, status.GetProperty("runs")[0].GetProperty("objectKey").GetString());
        Assert.Equal(JsonValueKind.String, status.GetProperty("lastSucceededAt").ValueKind);
    }

    [Fact]
    public async Task A_run_with_incomplete_settings_is_a_conflict()
    {
        await using var app = NewApp(out var storage);

        var response = await app.CreateClient().PostAsync("/api/backup/run", null, Ct);

        await HttpAssert.StatusAsync(HttpStatusCode.Conflict, response);
        Assert.Empty(storage.Uploads);
    }

    [Fact]
    public async Task A_failed_upload_is_recorded_and_announced_once_a_day()
    {
        await using var app = NewApp(out var storage);
        var admin = app.CreateClient();
        await CreateRuleAsync(admin, await CreateChannelAsync(admin, "system", "111"), "BackupFailed", null);
        var browser = await SignedInAdminAsync(app);
        await SaveAsync(browser, Settings(await ConfirmAsync(browser), enabled: false));
        storage.Failure = "The bucket refused the archive: AccessDenied.";

        var first = await (await admin.PostAsync("/api/backup/run", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
        await admin.PostAsync("/api/backup/run", null, Ct);

        Assert.Equal("Failed", first.GetProperty("outcome").GetString());
        Assert.Equal(storage.Failure, first.GetProperty("error").GetString());
        var history = await admin.GetFromJsonAsync<JsonElement[]>("/api/notifications", AggregatorApp.Json, Ct);
        Assert.Contains("AccessDenied", Assert.Single(history!).GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Status_says_when_no_rule_covers_failures_or_changes()
    {
        await using var app = NewApp(out _);
        var admin = app.CreateClient();
        await CreateRuleAsync(admin, await CreateChannelAsync(admin, "system", "111"), "BackupFailed", null);

        var status = await admin.GetFromJsonAsync<JsonElement>("/api/backup", AggregatorApp.Json, Ct);

        Assert.True(status.GetProperty("notifiesFailures").GetBoolean());
        Assert.False(status.GetProperty("notifiesChanges").GetBoolean());
    }

    private static AggregatorApp NewApp(out FakeBackupStorage storage, Action<IWebHostBuilder>? customize = null)
    {
        var fake = new FakeBackupStorage();
        storage = fake;
        return new AggregatorApp
        {
            Customize = builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IBackupStorage>();
                    services.AddSingleton<IBackupStorage>(fake);
                    services.RemoveAll<IBackupArchiver>();
                    services.AddSingleton<IBackupArchiver, FakeBackupArchiver>();
                });
                customize?.Invoke(builder);
            },
        };
    }

    private static Task<HttpClient> SignedInAdminAsync(AggregatorApp app) =>
        SignedInAsync(app, AggregatorApp.AdminUserName, AggregatorApp.AdminPassword);

    private static async Task<HttpClient> SignedInAsync(AggregatorApp app, string userName, string password)
    {
        var browser = app.CreateBrowserClient();
        await HttpAssert.StatusAsync(
            HttpStatusCode.OK,
            browser.PostAsJsonAsync("/api/auth/login", new { userName, password, rememberMe = false }, Ct));
        return browser;
    }

    private static async Task<string> ConfirmAsync(HttpClient browser)
    {
        var response = await browser.PostAsJsonAsync("/api/auth/confirm/password", new { password = AggregatorApp.AdminPassword }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("token").GetString()!;
    }

    private static async Task<JsonElement> SaveAsync(HttpClient browser, object settings)
    {
        var response = await browser.PutAsJsonAsync("/api/settings/backup", settings, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, response);
        return await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct);
    }

    private static async Task<Guid> CreateChannelAsync(HttpClient admin, string name, string discordChannelId)
    {
        var response = await admin.PostAsJsonAsync("/api/notification-channels", new { name, discordChannelId }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(AggregatorApp.Json, Ct)).GetProperty("id").GetGuid();
    }

    private static async Task CreateRuleAsync(HttpClient admin, Guid channel, string kind, Guid? environmentId)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/notification-channels/{channel}/rules", new { events = new[] { kind }, environmentId, stackId = (Guid?)null }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, response);
    }

    internal sealed class FakeBackupArchiver : IBackupArchiver
    {
        public Task<BackupArchive> CreateAsync(string recipient, CancellationToken cancellationToken) =>
            Task.FromResult(new BackupArchive("/nowhere", 123, () => { }));
    }

    internal sealed class FakeBackupStorage : IBackupStorage
    {
        public List<(string Key, BackupDestination Destination)> Uploads { get; } = [];

        public string? Failure { get; set; }

        public Task UploadAsync(BackupDestination destination, string filePath, string objectKey, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw new BackupFailedException(Failure);
            }

            Uploads.Add((objectKey, destination));
            return Task.CompletedTask;
        }
    }
}
