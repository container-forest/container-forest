using System.Globalization;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;

namespace ContainerForest.Aggregator.Application.Notifications;

/// <summary>A rule with the names of its scope, for showing it.</summary>
/// <param name="EnvironmentName">Null for a rule on the whole system.</param>
/// <param name="StackName">Null unless the rule is on one stack.</param>
public sealed record NotificationRuleView(NotificationRule Rule, string? EnvironmentName, string? StackName);

/// <param name="ChannelName">Null when the channel has been deleted since.</param>
public sealed record NotificationHistoryEntry(Notification Notification, string? ChannelName);

/// <summary>
/// Channels and rules of notifications, and their history. Administrators only: a Discord channel can be read by
/// people without any role in the system, so whoever routes events there decides what they see.
/// </summary>
public sealed class NotificationSettingsService(
    CurrentActor currentActor,
    INotificationChannelRepository channels,
    INotificationRuleRepository rules,
    INotificationRepository notifications,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    public const int DefaultHistoryLimit = 100;
    public const int MaxHistoryLimit = 500;

    public async Task<IReadOnlyList<NotificationChannel>> ListChannelsAsync(CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        return await channels.ListAsync(cancellationToken);
    }

    /// <param name="discordChannelId">The channel's id as Discord shows it (Developer Mode → Copy Channel ID).</param>
    public async Task<NotificationChannel> CreateChannelAsync(string name, string discordChannelId, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var channel = NotificationChannel.CreateDiscord(name, ParseDiscordId(discordChannelId), time.GetUtcNow());
        channels.Add(channel);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return channel;
    }

    /// <summary>
    /// Renames, moves or switches the channel. Pending notifications follow it: the bot reads the Discord channel
    /// when it sends, and a disabled channel's notifications fail.
    /// </summary>
    public async Task<NotificationChannel> UpdateChannelAsync(
        NotificationChannelId id, string name, string discordChannelId, bool isEnabled, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var channel = await channels.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Notification channel", id);

        var discordId = ParseDiscordId(discordChannelId);
        channel.Rename(name);
        channel.MoveToDiscordChannel(discordId);
        if (isEnabled)
        {
            channel.Enable();
        }
        else
        {
            channel.Disable();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return channel;
    }

    /// <summary>Deletes the channel with its rules; its history stays (it has no foreign keys).</summary>
    public async Task DeleteChannelAsync(NotificationChannelId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var channel = await channels.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Notification channel", id);
        channels.Remove(channel);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The channel's rules, oldest first.</summary>
    public async Task<IReadOnlyList<NotificationRuleView>> ListRulesAsync(NotificationChannelId channelId, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        _ = await channels.FindAsync(channelId, cancellationToken) ?? throw new NotFoundException("Notification channel", channelId);

        var views = new List<NotificationRuleView>();
        foreach (var rule in await rules.ListByChannelAsync(channelId, cancellationToken))
        {
            views.Add(await DescribeAsync(rule, cancellationToken));
        }

        return views;
    }

    /// <summary>
    /// Adds a rule: on one stack when <paramref name="stackId"/> is given, on one environment when only
    /// <paramref name="environmentId"/> is, otherwise on the whole system.
    /// </summary>
    public async Task<NotificationRuleView> CreateRuleAsync(
        NotificationChannelId channelId,
        NotificationEventKinds events,
        EnvironmentId? environmentId,
        StackId? stackId,
        CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var channel = await channels.FindAsync(channelId, cancellationToken) ?? throw new NotFoundException("Notification channel", channelId);
        var now = time.GetUtcNow();

        NotificationRule rule;
        if (stackId is { } sid)
        {
            var stack = await stacks.FindAsync(sid, cancellationToken) ?? throw new NotFoundException("Stack", sid);
            if (environmentId is { } eid && eid != stack.EnvironmentId)
            {
                throw new DomainValidationException($"Stack '{stack.Name}' belongs to another environment.");
            }

            rule = NotificationRule.ForStack(channel, stack, events, now);
        }
        else if (environmentId is { } eid)
        {
            var environment = await environments.FindAsync(eid, cancellationToken) ?? throw new NotFoundException("Environment", eid);
            rule = NotificationRule.ForEnvironment(channel, environment, events, now);
        }
        else
        {
            rule = NotificationRule.ForEverything(channel, events, now);
        }

        rules.Add(rule);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await DescribeAsync(rule, cancellationToken);
    }

    /// <summary>Changes what the rule subscribes to and switches it; its scope stays (delete and add a rule instead).</summary>
    public async Task<NotificationRuleView> UpdateRuleAsync(
        NotificationRuleId id, NotificationEventKinds events, bool isEnabled, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var rule = await rules.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Notification rule", id);

        rule.ChangeEvents(events);
        if (isEnabled)
        {
            rule.Enable();
        }
        else
        {
            rule.Disable();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await DescribeAsync(rule, cancellationToken);
    }

    public async Task DeleteRuleAsync(NotificationRuleId id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var rule = await rules.FindAsync(id, cancellationToken) ?? throw new NotFoundException("Notification rule", id);
        rules.Remove(rule);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The latest notifications of every channel, newest first.</summary>
    /// <param name="limit"><see cref="DefaultHistoryLimit"/> when null.</param>
    public async Task<IReadOnlyList<NotificationHistoryEntry>> ListHistoryAsync(int? limit, CancellationToken cancellationToken)
    {
        var count = limit ?? DefaultHistoryLimit;
        if (count is < 1 or > MaxHistoryLimit)
        {
            throw new DomainValidationException($"The limit must be between 1 and {MaxHistoryLimit}.");
        }

        await currentActor.GetAdministratorAsync(cancellationToken);
        var names = (await channels.ListAsync(cancellationToken)).ToDictionary(c => c.Id, c => c.Name);
        return (await notifications.ListRecentAsync(count, cancellationToken))
            .Select(n => new NotificationHistoryEntry(n, names.GetValueOrDefault(n.ChannelId)))
            .ToList();
    }

    private async Task<NotificationRuleView> DescribeAsync(NotificationRule rule, CancellationToken cancellationToken)
    {
        var environment = rule.EnvironmentId is { } eid ? await environments.FindAsync(eid, cancellationToken) : null;
        var stack = rule.StackId is { } sid ? await stacks.FindAsync(sid, cancellationToken) : null;
        return new NotificationRuleView(rule, environment?.Name, stack?.Name.Value);
    }

    /// <summary>Discord ids are 64-bit numbers; they travel as strings, since JavaScript numbers lose their last digits.</summary>
    private static ulong ParseDiscordId(string value) =>
        ulong.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0
            ? id
            : throw new DomainValidationException("Discord channel id must be a number, as Discord's Copy Channel ID gives it.");
}
