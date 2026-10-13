using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Api;

public sealed partial class ForestApi
{
    /// <summary>Kinds of events a notification rule can match; NotificationEventKinds in the aggregator.</summary>
    public static readonly IReadOnlyList<string> NotificationEvents =
        ["DeploymentSucceeded", "DeploymentFailed", "ContainerDied", "ContainerUnhealthy", "AgentOffline", "AgentOnline", "ContainerHealthy"];

    public async Task<IReadOnlyList<NotificationChannelResponse>> ListNotificationChannelsAsync(CancellationToken cancellationToken) =>
        await Api.NotificationChannels.GetAsync(cancellationToken: cancellationToken) ?? [];

    /// <exception cref="CliException">There is no channel with this name or id.</exception>
    public async Task<NotificationChannelResponse> FindNotificationChannelAsync(string nameOrId, CancellationToken cancellationToken)
    {
        var channels = await ListNotificationChannelsAsync(cancellationToken);
        return channels.FirstOrDefault(c => c.Id?.ToString() == nameOrId || string.Equals(c.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliException($"There is no notification channel '{nameOrId}'; see 'forest notification channel list'.", ExitCodes.Denied);
    }

    public async Task<NotificationChannelResponse> AddNotificationChannelAsync(string name, string discordChannelId, CancellationToken cancellationToken) =>
        Required(
            await Api.NotificationChannels.PostAsync(
                new CreateNotificationChannelRequest { Name = name, DiscordChannelId = discordChannelId }, cancellationToken: cancellationToken),
            "notification channel");

    public async Task<NotificationChannelResponse> UpdateNotificationChannelAsync(
        Guid id, string name, string discordChannelId, bool isEnabled, CancellationToken cancellationToken) =>
        Required(
            await Api.NotificationChannels[id].PutAsync(
                new UpdateNotificationChannelRequest { Name = name, DiscordChannelId = discordChannelId, IsEnabled = isEnabled },
                cancellationToken: cancellationToken),
            "notification channel");

    public Task RemoveNotificationChannelAsync(Guid id, CancellationToken cancellationToken) =>
        Api.NotificationChannels[id].DeleteAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<NotificationRuleResponse>> ListNotificationRulesAsync(Guid channelId, CancellationToken cancellationToken) =>
        await Api.NotificationChannels[channelId].Rules.GetAsync(cancellationToken: cancellationToken) ?? [];

    /// <param name="environmentId">Null for the whole system.</param>
    /// <param name="stackId">Null for the whole environment.</param>
    public async Task<NotificationRuleResponse> AddNotificationRuleAsync(
        Guid channelId, IReadOnlyList<string> events, Guid? environmentId, Guid? stackId, CancellationToken cancellationToken) =>
        Required(
            await Api.NotificationChannels[channelId].Rules.PostAsync(
                new CreateNotificationRuleRequest { Events = [.. events], EnvironmentId = environmentId, StackId = stackId },
                cancellationToken: cancellationToken),
            "notification rule");

    public async Task<NotificationRuleResponse> UpdateNotificationRuleAsync(
        Guid ruleId, IReadOnlyList<string> events, bool isEnabled, CancellationToken cancellationToken) =>
        Required(
            await Api.NotificationRules[ruleId].PutAsync(
                new UpdateNotificationRuleRequest { Events = [.. events], IsEnabled = isEnabled }, cancellationToken: cancellationToken),
            "notification rule");

    public Task RemoveNotificationRuleAsync(Guid ruleId, CancellationToken cancellationToken) =>
        Api.NotificationRules[ruleId].DeleteAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<NotificationResponse>> ListNotificationsAsync(int limit, CancellationToken cancellationToken) =>
        await Api.Notifications.GetAsync(c => c.QueryParameters.Limit = limit, cancellationToken) ?? [];
}
