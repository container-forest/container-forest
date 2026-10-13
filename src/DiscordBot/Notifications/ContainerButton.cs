using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using ContainerForest.DiscordBot.Commands;
using ContainerForest.DiscordBot.Forest;

namespace ContainerForest.DiscordBot.Notifications;

/// <summary>What a button under a container notification does.</summary>
public enum ContainerButtonAction
{
    Restart,
    Logs,
}

/// <summary>
/// A button under a container notification, written into its custom id as
/// <c>ntf:&lt;action&gt;:&lt;environment&gt;:&lt;container name&gt;</c>. The bot keeps no state: an old message's
/// buttons work after the bot restarts, and they act on whatever container has that name now, as /restart would.
/// </summary>
/// <remarks>
/// The container goes by name, not id: compose gives a recreated container a new id and the same name. Anyone in
/// the channel can click; the aggregator checks the clicker's own permissions, so a forged id gains nothing.
/// </remarks>
public sealed partial record ContainerButton(ContainerButtonAction Action, Guid EnvironmentId, string ContainerName)
{
    public const string Prefix = "ntf";

    /// <summary>Discord rejects longer custom ids.</summary>
    public const int MaxCustomIdLength = 100;

    private const string RestartKind = "rs";
    private const string LogsKind = "lg";
    private const char Separator = ':';

    /// <summary>
    /// The buttons of a container that died or turned unhealthy: "Restart", unless it may only be started, and "Logs".
    /// </summary>
    /// <returns>Empty also for a name that would not fit the custom id; the notification is then posted without buttons.</returns>
    public static IReadOnlyList<ContainerButton> For(ForestNotification notification)
    {
        if (notification.Kind is not (NotificationKind.ContainerDied or NotificationKind.ContainerUnhealthy)
            || notification.Container is not { } container
            || !Guid.TryParse(notification.EnvironmentId, out var environmentId)
            || !IsContainerName(container.ContainerName))
        {
            return [];
        }

        // Every action's id has the same length, so one check covers them all.
        var logs = new ContainerButton(ContainerButtonAction.Logs, environmentId, container.ContainerName);
        if (logs.ToCustomId().Length > MaxCustomIdLength)
        {
            return [];
        }

        return container.IsSystem ? [logs] : [logs with { Action = ContainerButtonAction.Restart }, logs];
    }

    public string ToCustomId() =>
        string.Join(Separator, Prefix, KindOf(Action), CompactGuid.Encode(EnvironmentId), ContainerName);

    /// <summary>Reads an id written by <see cref="ToCustomId"/>; anything else, e.g. from a future bot, is false.</summary>
    public static bool TryParse(string customId, [NotNullWhen(true)] out ContainerButton? button)
    {
        button = null;
        var parts = customId.Split(Separator);
        ContainerButtonAction? action = parts is [Prefix, var kind, _, _] ? ActionOf(kind) : null;
        if (action is null
            || !CompactGuid.TryDecode(parts[2], out var environmentId)
            || !IsContainerName(parts[3]))
        {
            return false;
        }

        button = new ContainerButton(action.Value, environmentId, parts[3]);
        return true;
    }

    private static string KindOf(ContainerButtonAction action) => action switch
    {
        ContainerButtonAction.Restart => RestartKind,
        ContainerButtonAction.Logs => LogsKind,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private static ContainerButtonAction? ActionOf(string kind) => kind switch
    {
        RestartKind => ContainerButtonAction.Restart,
        LogsKind => ContainerButtonAction.Logs,
        _ => null,
    };

    /// <summary>Docker's rule for container names; it also keeps the separator out of the name.</summary>
    private static bool IsContainerName(string name) => ContainerNamePattern().IsMatch(name);

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerNamePattern();
}
