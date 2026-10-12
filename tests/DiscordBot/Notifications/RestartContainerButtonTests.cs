extern alias bot;

using bot::ContainerForest.DiscordBot.Forest;
using bot::ContainerForest.DiscordBot.Interactions;
using bot::ContainerForest.DiscordBot.Notifications;
using Discord;

namespace ContainerForest.DiscordBot.Tests.Notifications;

public sealed class RestartContainerButtonTests
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
    public void ContainerThatDiedOrIsUnhealthy_GetsAButton_ThatReadsBack(NotificationKind kind)
    {
        Assert.True(RestartContainerButton.TryCreate(Died with { Kind = kind }, out var button));

        var id = button.ToCustomId();

        Assert.Equal(new RestartContainerButton(Environment, "web-app-1"), button);
        Assert.StartsWith("ntf:rs:", id, StringComparison.Ordinal);
        Assert.True(RestartContainerButton.TryParse(id, out var parsed));
        Assert.Equal(button, parsed);
    }

    [Fact]
    public void SystemContainer_GetsNoButton()
    {
        // It may only be started: restarting the aggregator, Caddy or an agent would cut a host off.
        Assert.False(RestartContainerButton.TryCreate(Died with { Container = Died.Container! with { IsSystem = true } }, out _));
    }

    [Theory]
    [InlineData(NotificationKind.DeploymentFailed)]
    [InlineData(NotificationKind.AgentOffline)]
    [InlineData(NotificationKind.Unknown)]
    public void OtherKinds_GetNoButton(NotificationKind kind)
    {
        Assert.False(RestartContainerButton.TryCreate(Died with { Kind = kind }, out _));
    }

    [Fact]
    public void NameThatDoesNotFitTheCustomId_GetsNoButton()
    {
        var longest = new string('a', RestartContainerButton.MaxCustomIdLength - "ntf:rs:".Length - 22 - 1);

        Assert.True(RestartContainerButton.TryCreate(Died with { Container = Died.Container! with { ContainerName = longest } }, out var fits));
        Assert.Equal(RestartContainerButton.MaxCustomIdLength, fits.ToCustomId().Length);
        Assert.False(RestartContainerButton.TryCreate(Died with { Container = Died.Container! with { ContainerName = longest + "a" } }, out _));
    }

    [Fact]
    public void NotificationWithoutAReadableEnvironment_GetsNoButton()
    {
        Assert.False(RestartContainerButton.TryCreate(Died with { EnvironmentId = "env-1" }, out _));
    }

    [Theory]
    [InlineData("ntf:rs:AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("ntf:rs:AAAAAAAAAAAAAAAAAAAAAA:web:extra")]
    [InlineData("ntf:rs:short:web-app-1")]
    [InlineData("ntf:rs:AAAAAAAAAAAAAAAAAAAAAA:-web")]
    [InlineData("ntf:xx:AAAAAAAAAAAAAAAAAAAAAA:web-app-1")]
    [InlineData("dash:rs:AAAAAAAAAAAAAAAAAAAAAA:web-app-1")]
    public void MalformedIds_AreNotRead(string customId)
    {
        Assert.False(RestartContainerButton.TryParse(customId, out _));
    }

    [Fact]
    public void Embed_HasTheRestartButtonOnlyWhenThereIsOne()
    {
        var buttons = NotificationEmbed.Buttons(Died);

        var button = Assert.IsType<ButtonComponent>(Assert.Single(Assert.IsType<ActionRowComponent>(Assert.Single(buttons!.Components)).Components));
        Assert.Equal("Restart", button.Label);
        Assert.Equal(ButtonStyle.Secondary, button.Style);
        Assert.Equal(new RestartContainerButton(Environment, "web-app-1").ToCustomId(), button.CustomId);
        Assert.Null(NotificationEmbed.Buttons(Died with { Kind = NotificationKind.AgentOffline, Container = null }));
    }
}
