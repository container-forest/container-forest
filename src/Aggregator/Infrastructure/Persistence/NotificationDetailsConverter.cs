using System.Text.Json;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ContainerForest.Aggregator.Infrastructure.Persistence;

/// <summary>
/// Stores <see cref="NotificationDetails"/> as one JSON column. Every kind of details has its own shape, and
/// they are only ever read whole, so a table per kind would add joins without any query that needs them.
/// </summary>
/// <remarks>The JSON is this class's own format with a "type" field, so the domain types need no serializer attributes.</remarks>
internal sealed class NotificationDetailsConverter()
    : ValueConverter<NotificationDetails, string>(details => Serialize(details), json => Deserialize(json))
{
    private const string DeploymentType = "deployment";
    private const string ContainerType = "container";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Serialize(NotificationDetails details) => details switch
    {
        DeploymentNotificationDetails d => JsonSerializer.Serialize(
            new StoredDetails(
                DeploymentType, d.DeploymentId.Value, d.StackId.Value, d.StackName.Value, d.Commit?.Value, d.PulledImages, d.Error),
            Options),
        ContainerNotificationDetails c => JsonSerializer.Serialize(
            new StoredDetails(ContainerType, null, null, c.StackName?.Value, null, false, null)
            {
                ContainerId = c.ContainerId,
                ContainerName = c.ContainerName,
                ServiceName = c.ServiceName,
                ExitCode = c.ExitCode,
                OutOfMemory = c.OutOfMemory,
                IsSystem = c.IsSystem,
            },
            Options),
        _ => throw new NotSupportedException($"{details.GetType().Name} cannot be stored yet."),
    };

    private static NotificationDetails Deserialize(string json)
    {
        var stored = JsonSerializer.Deserialize<StoredDetails>(json, Options)
            ?? throw new InvalidOperationException("Stored notification details are null.");
        return stored.Type switch
        {
            DeploymentType => new DeploymentNotificationDetails(
                DeploymentId.From(stored.DeploymentId!.Value),
                StackId.From(stored.StackId!.Value),
                StackName.Parse(stored.StackName!),
                stored.Commit is null ? null : CommitSha.Parse(stored.Commit),
                stored.PulledImages,
                stored.Error),
            ContainerType => new ContainerNotificationDetails(
                stored.ContainerId!,
                stored.ContainerName!,
                stored.StackName is null ? null : StackName.Parse(stored.StackName),
                stored.ServiceName,
                stored.ExitCode,
                stored.OutOfMemory,
                stored.IsSystem),
            _ => throw new InvalidOperationException($"Unknown notification details type '{stored.Type}'."),
        };
    }

    /// <summary>The union of all kinds' fields; each kind uses its own. Fields added later are optional, so older rows still read.</summary>
    private sealed record StoredDetails(
        string Type, Guid? DeploymentId, Guid? StackId, string? StackName, string? Commit, bool PulledImages, string? Error)
    {
        public string? ContainerId { get; init; }

        public string? ContainerName { get; init; }

        public string? ServiceName { get; init; }

        public int? ExitCode { get; init; }

        public bool OutOfMemory { get; init; }

        public bool IsSystem { get; init; }
    }
}
