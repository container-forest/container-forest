extern alias bot;

using bot::ContainerForest.DiscordBot.Forest;
using bot::ContainerForest.DiscordBot.Interactions;

namespace ContainerForest.DiscordBot.Tests.Notifications;

public sealed class NotificationEmbedTests
{
    private static readonly ForestNotification Failed = new(
        "n-1",
        42,
        NotificationKind.DeploymentFailed,
        NotificationSeverity.Error,
        new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        "env-1",
        "prod_eu",
        "Deployment of stack 'web' on 'prod_eu' failed: compose failed",
        Deployment: new DeploymentEvent("d-1", "s-1", "web", "0123456789abcdef0123456789abcdef01234567", true, "compose failed:\nservice db"));

    [Fact]
    public void Deployment_ShowsSummaryEnvironmentStackCommitAndError()
    {
        var embed = NotificationEmbed.Build(Failed);

        Assert.Equal("❌ Deployment failed", embed.Title);
        Assert.Equal(@"Deployment of stack 'web' on 'prod\_eu' failed: compose failed", embed.Description);
        Assert.Equal(Failed.OccurredAt, embed.Timestamp);
        Assert.Equal(0xE74C3Cu, embed.Color!.Value.RawValue);
        Assert.Equal(
            [
                ("Environment", @"prod\_eu"),
                ("Stack", "web"),
                ("Commit", "`0123456`"),
                ("Images", "pulled"),
                ("Error", "```\ncompose failed:\nservice db\n```"),
            ],
            embed.Fields.Select(f => (f.Name, f.Value)));
    }

    [Fact]
    public void LongError_IsCutToTheFieldLimit_AndCannotCloseTheCodeBlock()
    {
        var error = "```" + new string('x', 3000);

        var embed = NotificationEmbed.Build(Failed with { Deployment = Failed.Deployment! with { Error = error } });

        var field = Assert.Single(embed.Fields, f => f.Name == "Error").Value;
        Assert.True(field.Length <= NotificationEmbed.MaxFieldLength);
        Assert.StartsWith("```\n'''x", field, StringComparison.Ordinal);
        Assert.EndsWith("…\n```", field, StringComparison.Ordinal);
    }

    [Fact]
    public void Success_IsGreen_AndHasNoError()
    {
        var succeeded = Failed with
        {
            Kind = NotificationKind.DeploymentSucceeded,
            Severity = NotificationSeverity.Info,
            Deployment = Failed.Deployment! with { Error = null, PulledImages = false },
        };

        var embed = NotificationEmbed.Build(succeeded);

        Assert.Equal("✅ Deployment succeeded", embed.Title);
        Assert.Equal(0x2ECC71u, embed.Color!.Value.RawValue);
        Assert.Equal(["Environment", "Stack", "Commit"], embed.Fields.Select(f => f.Name));
    }

    [Fact]
    public void ContainerDetails_AndUnknownKinds_AreShown()
    {
        var died = Failed with
        {
            Kind = NotificationKind.Unknown,
            Severity = NotificationSeverity.Unknown,
            Deployment = null,
            Container = new ContainerEvent("4f1c", "web-app-1", "web", "app", 137),
        };

        var embed = NotificationEmbed.Build(died);

        Assert.Equal("Notification", embed.Title);
        Assert.Equal(
            [("Environment", @"prod\_eu"), ("Container", "`web-app-1`"), ("Stack", "web"), ("Exit code", "137")],
            embed.Fields.Select(f => (f.Name, f.Value)));
    }

    [Fact]
    public void Restart_is_noted_once_with_the_latest_user_and_the_rest_kept()
    {
        var notification = NotificationEmbed.Build(Failed);
        var first = new DateTimeOffset(2026, 10, 1, 12, 5, 0, TimeSpan.Zero);

        var noted = NotificationEmbed.WithRestart(notification, 111, first);
        var renoted = NotificationEmbed.WithRestart(noted, 222, first.AddMinutes(3));

        Assert.Equal(
            $"🔄 Restarted by <@111> <t:{first.ToUnixTimeSeconds()}:R>",
            Assert.Single(noted.Fields, f => f.Name == NotificationEmbed.LastActionField).Value);
        Assert.Equal(
            $"🔄 Restarted by <@222> <t:{first.AddMinutes(3).ToUnixTimeSeconds()}:R>",
            Assert.Single(renoted.Fields, f => f.Name == NotificationEmbed.LastActionField).Value);
        Assert.Equal(
            notification.Fields.Select(f => (f.Name, f.Value)),
            renoted.Fields.Where(f => f.Name != NotificationEmbed.LastActionField).Select(f => (f.Name, f.Value)));
        Assert.Equal((notification.Title, notification.Description, notification.Color, notification.Timestamp),
            (renoted.Title, renoted.Description, renoted.Color, renoted.Timestamp));
    }

    [Fact]
    public void Healthy_again_is_green_with_the_container_and_no_buttons()
    {
        var healthy = Failed with
        {
            Kind = NotificationKind.ContainerHealthy,
            Severity = NotificationSeverity.Info,
            Summary = "Container 'web-app-1' on 'prod_eu' is healthy again.",
            Deployment = null,
            Container = new ContainerEvent("4f1c2a9e8b7d", "web-app-1", null, null, null),
        };

        var embed = NotificationEmbed.Build(healthy);

        Assert.Equal("💚 Container healthy again", embed.Title);
        Assert.Equal(0x2ECC71u, embed.Color!.Value.RawValue);
        Assert.Contains(embed.Fields, f => (f.Name, f.Value) == ("Container", "`web-app-1`"));
        Assert.Null(NotificationEmbed.Buttons(healthy));
    }
}
