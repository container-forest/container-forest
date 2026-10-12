using ContainerForest.DiscordBot.Commands;
using ContainerForest.DiscordBot.Forest;
using ContainerForest.DiscordBot.Notifications;
using Discord;

namespace ContainerForest.DiscordBot.Interactions;

/// <summary>The Discord message for a notification: the summary as the headline, details as fields.</summary>
public static class NotificationEmbed
{
    /// <summary>Discord's limit for one field's value.</summary>
    public const int MaxFieldLength = 1024;

    public static Embed Build(ForestNotification notification)
    {
        var embed = new EmbedBuilder()
            .WithTitle(Title(notification.Kind))
            .WithDescription(Markdown.Escape(notification.Summary))
            .WithColor(ColorOf(notification.Severity))
            .WithTimestamp(notification.OccurredAt)
            .AddField("Environment", Markdown.Escape(notification.EnvironmentName), inline: true);

        if (notification.Deployment is { } deployment)
        {
            embed.AddField("Stack", Markdown.Escape(deployment.StackName), inline: true);
            if (deployment.CommitSha is { } commit)
            {
                embed.AddField("Commit", Markdown.Code(commit.Length > 7 ? commit[..7] : commit), inline: true);
            }

            if (deployment.PulledImages)
            {
                embed.AddField("Images", "pulled", inline: true);
            }

            if (deployment.Error is { } error)
            {
                // The summary has the reason on one line; here it keeps its lines, e.g. compose output.
                embed.AddField("Error", CodeBlock(error));
            }
        }

        if (notification.Container is { } container)
        {
            embed.AddField("Container", Markdown.Code(container.ContainerName), inline: true);
            if (container.StackName is { } stack)
            {
                embed.AddField("Stack", Markdown.Escape(stack), inline: true);
            }

            if (container.ExitCode is { } exitCode)
            {
                embed.AddField("Exit code", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), inline: true);
            }
        }

        return embed.Build();
    }

    /// <summary>The buttons under the message: "Restart" for a container that died or turned unhealthy; null when there are none.</summary>
    public static MessageComponent? Buttons(ForestNotification notification) =>
        RestartContainerButton.TryCreate(notification, out var restart)
            ? new ComponentBuilder()
                .WithButton("Restart", restart.ToCustomId(), ButtonStyle.Secondary, new Emoji("🔄"))
                .Build()
            : null;

    private static string Title(NotificationKind kind) => kind switch
    {
        NotificationKind.DeploymentSucceeded => "✅ Deployment succeeded",
        NotificationKind.DeploymentFailed => "❌ Deployment failed",
        NotificationKind.ContainerDied => "💀 Container died",
        NotificationKind.ContainerUnhealthy => "⚠️ Container unhealthy",
        NotificationKind.AgentOffline => "🔌 Agent offline",
        NotificationKind.AgentOnline => "🔌 Agent back online",
        _ => "Notification",
    };

    private static Color ColorOf(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Info => new Color(0x2E, 0xCC, 0x71),
        NotificationSeverity.Warning => new Color(0xE6, 0x7E, 0x22),
        NotificationSeverity.Error => new Color(0xE7, 0x4C, 0x3C),
        _ => new Color(0x95, 0xA5, 0xA6),
    };

    /// <summary>A code block that fits a field; a backtick fence inside the text would end it early, so fences are broken up.</summary>
    private static string CodeBlock(string text)
    {
        const string fence = "```";
        var body = text.Replace(fence, "'''", StringComparison.Ordinal).Trim();
        var room = MaxFieldLength - (2 * fence.Length) - 2;
        if (body.Length > room)
        {
            body = body[..(room - 1)] + "…";
        }

        return $"{fence}\n{body}\n{fence}";
    }
}
