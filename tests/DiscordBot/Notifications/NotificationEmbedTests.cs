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
}
