using ContainerForest.Contracts.Agent;
using Docker.DotNet.Models;

namespace ContainerForest.Agent.Docker;

/// <summary>Converts Docker's container summaries into the protocol's <see cref="ContainerInfo"/>.</summary>
public static class ContainerInfoMapper
{
    public const string ProjectLabel = "com.docker.compose.project";
    public const string ServiceLabel = "com.docker.compose.service";

    public static ContainerInfo ToContainerInfo(ContainerListResponse container) => new()
    {
        Id = container.ID ?? "",
        Name = ToName(container.Names),
        State = ToState(container.State),
        Image = container.Image ?? "",
        StackName = Label(container.Labels, ProjectLabel),
        ServiceName = Label(container.Labels, ServiceLabel),
        StatusText = container.Status ?? "",
        CreatedUnixMs = ToUnixMilliseconds(container.Created),
    };

    public static ContainerState ToState(string? state) => state?.ToLowerInvariant() switch
    {
        "created" => ContainerState.Created,
        "running" => ContainerState.Running,
        "paused" => ContainerState.Paused,
        "restarting" => ContainerState.Restarting,
        "removing" => ContainerState.Removing,
        "exited" => ContainerState.Exited,
        "dead" => ContainerState.Dead,
        _ => ContainerState.Unspecified,
    };

    // Docker lists names with a leading slash ("/web-app-1"); the first one is the container's own name.
    private static string ToName(IList<string>? names) =>
        names is { Count: > 0 } ? names[0].TrimStart('/') : "";

    private static string Label(IDictionary<string, string>? labels, string key) =>
        labels is not null && labels.TryGetValue(key, out var value) ? value : "";

    private static long ToUnixMilliseconds(DateTime created)
    {
        if (created == default)
        {
            return 0;
        }

        var utc = created.Kind == DateTimeKind.Local ? created.ToUniversalTime() : DateTime.SpecifyKind(created, DateTimeKind.Utc);
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }
}
