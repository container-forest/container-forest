using System.Globalization;
using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Notification channels, rules and history for the panel (see NotificationContracts).</summary>
internal static class NotificationEndpoints
{
    /// <summary>The single kinds a rule can subscribe to, in declaration order.</summary>
    private static readonly NotificationEventKinds[] SingleKinds = Enum.GetValues<NotificationEventKinds>()
        .Where(k => k != NotificationEventKinds.None && k != NotificationEventKinds.All)
        .ToArray();

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .WithTags("Notifications")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapGet("/notification-channels", async (NotificationSettingsService service, CancellationToken ct) =>
                (await service.ListChannelsAsync(ct)).Select(ToResponse).ToList())
            .WithName("ListNotificationChannels");

        api.MapPost(
                "/notification-channels",
                async (CreateNotificationChannelRequest request, NotificationSettingsService service, CancellationToken ct) =>
                {
                    var channel = await service.CreateChannelAsync(request.Name, request.DiscordChannelId, ct);
                    return TypedResults.Created($"/api/notification-channels/{channel.Id}", ToResponse(channel));
                })
            .WithName("CreateNotificationChannel")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapPut(
                "/notification-channels/{id:guid}",
                async (Guid id, UpdateNotificationChannelRequest request, NotificationSettingsService service, CancellationToken ct) =>
                    ToResponse(await service.UpdateChannelAsync(
                        NotificationChannelId.From(id), request.Name, request.DiscordChannelId, request.IsEnabled, ct)))
            .WithName("UpdateNotificationChannel")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapDelete("/notification-channels/{id:guid}", async (Guid id, NotificationSettingsService service, CancellationToken ct) =>
            {
                await service.DeleteChannelAsync(NotificationChannelId.From(id), ct);
                return TypedResults.NoContent();
            })
            .WithName("DeleteNotificationChannel")
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/notification-channels/{id:guid}/rules", async (Guid id, NotificationSettingsService service, CancellationToken ct) =>
                (await service.ListRulesAsync(NotificationChannelId.From(id), ct)).Select(ToResponse).ToList())
            .WithName("ListNotificationRules")
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost(
                "/notification-channels/{id:guid}/rules",
                async (Guid id, CreateNotificationRuleRequest request, NotificationSettingsService service, CancellationToken ct) =>
                {
                    var rule = await service.CreateRuleAsync(
                        NotificationChannelId.From(id),
                        Combine(request.Events),
                        request.EnvironmentId is { } environmentId ? EnvironmentId.From(environmentId) : null,
                        request.StackId is { } stackId ? StackId.From(stackId) : null,
                        ct);
                    return TypedResults.Created($"/api/notification-rules/{rule.Rule.Id}", ToResponse(rule));
                })
            .WithName("CreateNotificationRule")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut(
                "/notification-rules/{id:guid}",
                async (Guid id, UpdateNotificationRuleRequest request, NotificationSettingsService service, CancellationToken ct) =>
                    ToResponse(await service.UpdateRuleAsync(NotificationRuleId.From(id), Combine(request.Events), request.IsEnabled, ct)))
            .WithName("UpdateNotificationRule")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapDelete("/notification-rules/{id:guid}", async (Guid id, NotificationSettingsService service, CancellationToken ct) =>
            {
                await service.DeleteRuleAsync(NotificationRuleId.From(id), ct);
                return TypedResults.NoContent();
            })
            .WithName("DeleteNotificationRule")
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/notifications", async (int? limit, NotificationSettingsService service, CancellationToken ct) =>
                (await service.ListHistoryAsync(limit, ct))
                    .Select(e => new NotificationResponse(
                        e.Notification.Id.Value,
                        e.Notification.ChannelId.Value,
                        e.ChannelName,
                        e.Notification.Kind,
                        e.Notification.Severity,
                        e.Notification.EnvironmentId?.Value,
                        e.Notification.StackId?.Value,
                        e.Notification.OccurredAt,
                        e.Notification.Summary,
                        e.Notification.Status,
                        e.Notification.FinishedAt,
                        e.Notification.Error))
                    .ToList())
            .WithName("ListNotifications")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    /// <summary>The flags of a list of kinds; an empty list is None, which the rule rejects.</summary>
    private static NotificationEventKinds Combine(IReadOnlyList<NotificationEventKinds>? events) =>
        (events ?? []).Aggregate(NotificationEventKinds.None, (all, kind) => all | kind);

    private static List<NotificationEventKinds> Split(NotificationEventKinds events) =>
        SingleKinds.Where(k => events.HasFlag(k)).ToList();

    private static NotificationChannelResponse ToResponse(NotificationChannel channel) => new(
        channel.Id.Value,
        channel.Name,
        channel.Kind,
        channel.DiscordChannelId.ToString(CultureInfo.InvariantCulture),
        channel.IsEnabled,
        channel.CreatedAt);

    private static NotificationRuleResponse ToResponse(NotificationRuleView view) => new(
        view.Rule.Id.Value,
        view.Rule.ChannelId.Value,
        Split(view.Rule.Events),
        view.Rule.EnvironmentId?.Value,
        view.EnvironmentName,
        view.Rule.StackId?.Value,
        view.StackName,
        view.Rule.IsEnabled,
        view.Rule.CreatedAt);
}
