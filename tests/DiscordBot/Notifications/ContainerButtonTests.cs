extern alias bot;

using bot::ContainerForest.DiscordBot.Forest;
using bot::ContainerForest.DiscordBot.Interactions;
using bot::ContainerForest.DiscordBot.Notifications;
using Discord;

namespace ContainerForest.DiscordBot.Tests.Notifications;

public sealed class ContainerButtonTests
{
    private static readonly Guid Environment = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    private static readonly ForestNotification Died = new(
        "n-1",
        42,
        NotificationKind.ContainerDied,
        NotificationSeverity.Error,
        new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        Environment.ToString(),
        "prod",
        "Container 'web-app-1' of stack 'web' on 'prod' exited with code 1.",
        Container: new ContainerEvent("4f1c2a9e8b7d", "web-app-1", "web", "app", 1));

    [Theory]
    [InlineData(NotificationKind.ContainerDied)]
    [InlineData(NotificationKind.ContainerUnhealthy)]
    public void ContainerThatDiedOrIsUnhealthy_GetsRestartAndLogs_ThatReadBack(NotificationKind kind)
    {
        var buttons = ContainerButton.For(Died with { Kind = kind });

        Assert.Equal(
            [
                new ContainerButton(ContainerButtonAction.Restart, Environment, "web-app-1"),
                new ContainerButton(ContainerButtonAction.Logs, Environment, "web-app-1"),
            ],
            buttons);
        Assert.StartsWith("ntf:rs:", buttons[0].ToCustomId(), StringComparison.Ordinal);
        Assert.StartsWith("ntf:lg:", buttons[1].ToCustomId(), StringComparison.Ordinal);
        foreach (var button in buttons)
        {
            Assert.True(ContainerButton.TryParse(button.ToCustomId(), out var parsed));
            Assert.Equal(button, parsed);
        }
    }

    [Fact]
    public void SystemContainer_GetsOnlyLogs()
    {
        // It may only be started: restarting the aggregator, Caddy or an agent would cut a host off. Reading its log is fine.
        var buttons = ContainerButton.For(Died with { Container = Died.Container! with { IsSystem = true } });

        Assert.Equal(ContainerButtonAction.Logs, Assert.Single(buttons).Action);
    }

    [Theory]
    [InlineData(NotificationKind.DeploymentFailed)]
    [InlineData(NotificationKind.AgentOffline)]
    [InlineData(NotificationKind.Unknown)]
    public void OtherKinds_GetNoButtons(NotificationKind kind)
    {
        Assert.Empty(ContainerButton.For(Died with { Kind = kind }));
    }

    [Fact]
    public void NameThatDoesNotFitTheCustomId_GetsNoButtons()
    {
        var longest = new string('a', ContainerButton.MaxCustomIdLength - "ntf:rs:".Length - 22 - 1);

        var fits = ContainerButton.For(Died with { Container = Died.Container! with { ContainerName = longest } });
        Assert.Equal(2, fits.Count);
        Assert.All(fits, b => Assert.Equal(ContainerButton.MaxCustomIdLength, b.ToCustomId().Length));
        Assert.Empty(ContainerButton.For(Died with { Container = Died.Container! with { ContainerName = longest + "a" } }));
    }

    [Fact]
    public void NotificationWithoutAReadableEnvironment_GetsNoButtons()
    {
        Assert.Empty(ContainerButton.For(Died with { EnvironmentId = "env-1" }));
    }

    [Theory]
    [InlineData("ntf:rs:AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("ntf:rs:AAAAAAAAAAAAAAAAAAAAAA:web:extra")]
    [InlineData("ntf:rs:short:web-app-1")]
    [InlineData("ntf:lg:AAAAAAAAAAAAAAAAAAAAAA:-web")]
    [InlineData("ntf:xx:AAAAAAAAAAAAAAAAAAAAAA:web-app-1")]
    [InlineData("dash:rs:AAAAAAAAAAAAAAAAAAAAAA:web-app-1")]
    public void MalformedIds_AreNotRead(string customId)
    {
        Assert.False(ContainerButton.TryParse(customId, out _));
    }

    [Fact]
    public void Message_HasRestartThenLogs_OrNoRowAtAll()
    {
        var buttons = NotificationEmbed.Buttons(Died);

        var row = Assert.IsType<ActionRowComponent>(Assert.Single(buttons!.Components));
        Assert.Equal(
            [
                ("Restart", new ContainerButton(ContainerButtonAction.Restart, Environment, "web-app-1").ToCustomId()),
                ("Logs", new ContainerButton(ContainerButtonAction.Logs, Environment, "web-app-1").ToCustomId()),
            ],
            row.Components.Cast<ButtonComponent>().Select(b => (b.Label, b.CustomId)));
        Assert.All(row.Components.Cast<ButtonComponent>(), b => Assert.Equal(ButtonStyle.Secondary, b.Style));
        Assert.Null(NotificationEmbed.Buttons(Died with { Kind = NotificationKind.AgentOffline, Container = null }));
    }
}
