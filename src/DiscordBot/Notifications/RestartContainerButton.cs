using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using ContainerForest.DiscordBot.Commands;
using ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Notifications;

/// <summary>
/// The "Restart" button under a container notification, written into its custom id as
/// <c>ntf:rs:&lt;environment&gt;:&lt;container name&gt;</c>. The bot keeps no state: an old message's button works
/// after the bot restarts, and it restarts whatever container has that name now, as /restart would.
/// </summary>
/// <remarks>
/// The container goes by name, not id: compose gives a recreated container a new id and the same name. Anyone in
/// the channel can click; the aggregator checks the clicker's own permissions, so a forged id gains nothing.
/// </remarks>
public sealed partial record RestartContainerButton(Guid EnvironmentId, string ContainerName)
{
    public const string Prefix = "ntf";

    /// <summary>Discord rejects longer custom ids.</summary>
    public const int MaxCustomIdLength = 100;

    private const string Kind = "rs";
    private const char Separator = ':';

    /// <summary>The button for a notification that has one: a container that died or turned unhealthy, unless it may only be started.</summary>
    /// <returns>False also for a name that would not fit the custom id; the notification is then posted without the button.</returns>
    public static bool TryCreate(ForestNotification notification, [NotNullWhen(true)] out RestartContainerButton? button)
    {
        button = null;
        if (notification.Kind is not (NotificationKind.ContainerDied or NotificationKind.ContainerUnhealthy)
            || notification.Container is not { IsSystem: false } container
            || !Guid.TryParse(notification.EnvironmentId, out var environmentId))
        {
            return false;
        }

        var candidate = new RestartContainerButton(environmentId, container.ContainerName);
        if (!IsContainerName(container.ContainerName) || candidate.ToCustomId().Length > MaxCustomIdLength)
        {
            return false;
        }

        button = candidate;
        return true;
    }

    public string ToCustomId() => string.Join(Separator, Prefix, Kind, CompactGuid.Encode(EnvironmentId), ContainerName);

    /// <summary>Reads an id written by <see cref="ToCustomId"/>; anything else, e.g. from a future bot, is false.</summary>
    public static bool TryParse(string customId, [NotNullWhen(true)] out RestartContainerButton? button)
    {
        button = null;
        var parts = customId.Split(Separator);
        if (parts is not [Prefix, Kind, var environment, var name]
            || !CompactGuid.TryDecode(environment, out var environmentId)
            || !IsContainerName(name))
        {
            return false;
        }

        button = new RestartContainerButton(environmentId, name);
        return true;
    }

    /// <summary>Docker's rule for container names; it also keeps the separator out of the name.</summary>
    private static bool IsContainerName(string name) => ContainerNamePattern().IsMatch(name);

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerNamePattern();
}
