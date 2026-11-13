using ContainerForest.Contracts.Agent;
using Docker.DotNet.Models;

namespace ContainerForest.Agent.Docker;

/// <summary>Converts Docker's container summaries into the protocol's <see cref="ContainerInfo"/>.</summary>
public static class ContainerInfoMapper
{
    public const string ProjectLabel = "com.docker.compose.project";
    public const string ServiceLabel = "com.docker.compose.service";

    public const string WorkingDirectoryLabel = "com.docker.compose.project.working_dir";
    public const string ConfigFilesLabel = "com.docker.compose.project.config_files";
    public const string EnvFilesLabel = "com.docker.compose.project.environment_file";

    /// <summary>Set to "true" in deploy/*.yaml on the containers of container-forest itself.</summary>
    public const string SystemLabel = "com.container-forest.system";

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
        System = IsTrue(Label(container.Labels, SystemLabel)),
    };

    /// <returns>Null for a container compose did not create.</returns>
    public static ComposeContainer? ToComposeContainer(ContainerListResponse container)
    {
        var info = ToContainerInfo(container);
        if (info.StackName.Length == 0)
        {
            return null;
        }

        return new ComposeContainer(
            info,
            Label(container.Labels, WorkingDirectoryLabel),
            SplitList(Label(container.Labels, ConfigFilesLabel)),
            SplitList(Label(container.Labels, EnvFilesLabel)));
    }

    // Compose joins several paths with ','.
    private static IReadOnlyList<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

    // Compose writes label values as given; "true" in any case counts, anything else does not.
    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

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
