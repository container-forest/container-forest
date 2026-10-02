using System.Net;
using System.Net.Http.Json;

namespace ContainerForest.Aggregator.Api.Tests;

public class NotificationEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Channel_and_rules_are_managed_through_the_api()
    {
        await using var app = new AggregatorApp();
        var environmentId = (await app.CreateEnvironmentAsync("prod")).Id;
        var client = app.CreateClient();

        var created = await client.PostAsJsonAsync(
            "/api/notification-channels", new { name = "alerts", discordChannelId = "1234567890123456789" }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, created);
        var channel = (await created.Content.ReadFromJsonAsync<ChannelView>(AggregatorApp.Json, Ct))!;
        Assert.Equal("1234567890123456789", channel.DiscordChannelId);

        var rule = await client.PostAsJsonAsync(
            $"/api/notification-channels/{channel.Id}/rules",
            new { events = new[] { "DeploymentFailed", "AgentOffline" }, environmentId, stackId = (Guid?)null },
            Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.Created, rule);
        var ruleView = (await rule.Content.ReadFromJsonAsync<RuleView>(AggregatorApp.Json, Ct))!;
        Assert.Equal(["DeploymentFailed", "AgentOffline"], ruleView.Events);
        Assert.Equal("prod", ruleView.EnvironmentName);

        var updated = await client.PutAsJsonAsync(
            $"/api/notification-rules/{ruleView.Id}", new { events = new[] { "DeploymentSucceeded" }, isEnabled = false }, Ct);
        await HttpAssert.StatusAsync(HttpStatusCode.OK, updated);
        var rules = (await client.GetFromJsonAsync<RuleView[]>($"/api/notification-channels/{channel.Id}/rules", AggregatorApp.Json, Ct))!;
        Assert.Equal(["DeploymentSucceeded"], Assert.Single(rules).Events);
        Assert.False(rules[0].IsEnabled);

        // The rule goes with its channel.
        await HttpAssert.StatusAsync(HttpStatusCode.NoContent, client.DeleteAsync($"/api/notification-channels/{channel.Id}", Ct));
        Assert.Empty((await client.GetFromJsonAsync<ChannelView[]>("/api/notification-channels", AggregatorApp.Json, Ct))!);
        Assert.Empty((await client.GetFromJsonAsync<object[]>("/api/notifications", AggregatorApp.Json, Ct))!);
    }

    [Fact]
    public async Task Rule_without_events_or_with_a_malformed_body_is_a_bad_request_and_users_are_forbidden()
    {
        await using var app = new AggregatorApp();
        await app.CreateUserAsync("alice", isAdmin: false);
        var created = await app.CreateClient().PostAsJsonAsync("/api/notification-channels", new { name = "alerts", discordChannelId = "1" }, Ct);
        var channel = (await created.Content.ReadFromJsonAsync<ChannelView>(AggregatorApp.Json, Ct))!;

        await HttpAssert.StatusAsync(
            HttpStatusCode.BadRequest,
            app.CreateClient().PostAsJsonAsync($"/api/notification-channels/{channel.Id}/rules", new { events = Array.Empty<string>() }, Ct));
        await HttpAssert.StatusAsync(
            HttpStatusCode.BadRequest,
            app.CreateClient().PostAsJsonAsync($"/api/notification-channels/{channel.Id}/rules", new { events = new[] { "DeploymentFailed" }, environmentId = new { id = 1 } }, Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, app.CreateClientAs("alice").GetAsync("/api/notification-channels", Ct));
        await HttpAssert.StatusAsync(HttpStatusCode.Forbidden, app.CreateClientAs("alice").GetAsync("/api/notifications", Ct));
    }

    private sealed record ChannelView(Guid Id, string Name, string DiscordChannelId, bool IsEnabled);

    private sealed record RuleView(Guid Id, string[] Events, string? EnvironmentName, bool IsEnabled);
}
