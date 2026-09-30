using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Notifications;

public enum NotificationChannelKind
{
    /// <summary>A Discord text channel; the bot posts there (bot.proto, WatchNotifications).</summary>
    Discord,
}

/// <summary>Where notifications are delivered. Managed by administrators: a channel can be read by people without any role.</summary>
public sealed class NotificationChannel : Entity<NotificationChannelId>
{
    public const int MaxNameLength = 64;

    private NotificationChannel(
        NotificationChannelId id, string name, NotificationChannelKind kind, ulong discordChannelId, DateTimeOffset createdAt)
        : base(id)
    {
        Name = name;
        Kind = kind;
        DiscordChannelId = discordChannelId;
        IsEnabled = true;
        CreatedAt = createdAt;
    }

    // For EF Core.
    private NotificationChannel()
    {
        Name = null!;
    }

    public string Name { get; private set; }

    public NotificationChannelKind Kind { get; private init; }

    /// <summary>Id of the Discord text channel; the bot must be a member of its server and allowed to post.</summary>
    public ulong DiscordChannelId { get; private set; }

    /// <summary>A disabled channel receives nothing; its rules stay.</summary>
    public bool IsEnabled { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static NotificationChannel CreateDiscord(string name, ulong discordChannelId, DateTimeOffset now) =>
        new(NotificationChannelId.New(), NormalizeName(name), NotificationChannelKind.Discord, EnsureDiscordId(discordChannelId), now);

    public void Rename(string name) => Name = NormalizeName(name);

    public void MoveToDiscordChannel(ulong discordChannelId) => DiscordChannelId = EnsureDiscordId(discordChannelId);

    public void Enable() => IsEnabled = true;

    public void Disable() => IsEnabled = false;

    private static ulong EnsureDiscordId(ulong id) =>
        id != 0 ? id : throw new DomainValidationException("Discord channel id must be set.");

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            throw new DomainValidationException($"Channel name must be 1-{MaxNameLength} characters without control characters.");
        }

        return trimmed;
    }
}
