namespace ContainerForest.Aggregator.Domain.Policies;

/// <summary>
/// What the compose policy looks at in a stack, taken from compose's normalized model (ADR-0012). Nothing secret:
/// no environment values, commands or file contents, so the facts of the deployed version are kept on the stack and
/// checked again when the policy changes.
/// </summary>
/// <param name="StackDirectory">The stack's directory on the host, as the agent reported it.</param>
/// <param name="AgentDirectories">The agent's own directories on the host; no stack may mount them.</param>
/// <param name="Files">Host files of secrets and configs, which compose bind-mounts read-only.</param>
public sealed record ComposeFacts(
    string StackDirectory,
    IReadOnlyList<string> AgentDirectories,
    IReadOnlyList<ServiceFacts> Services,
    IReadOnlyList<NamedResource> Volumes,
    IReadOnlyList<NamedResource> Networks,
    IReadOnlyList<string> Files,
    IReadOnlyList<InspectedMount> InspectedMounts);

/// <param name="Namespaces">network_mode, pid, ipc, uts, userns_mode and cgroup as compose has them, e.g. "host" or "container:db".</param>
/// <param name="Devices">Host paths of devices.</param>
/// <param name="PublishedPorts">Host ports the service publishes; ranges are listed port by port.</param>
/// <param name="VolumesFrom">volumes_from entries: a service of the stack or "container:name".</param>
/// <param name="UnknownKeys">Service keys the policy does not know; compose may have added them after this version.</param>
public sealed record ServiceFacts(
    string Name,
    bool Privileged,
    IReadOnlyDictionary<string, string> Namespaces,
    IReadOnlyList<string> CapAdd,
    IReadOnlyList<string> SecurityOpt,
    IReadOnlyList<string> Devices,
    bool Gpus,
    bool DeviceCgroupRules,
    bool CgroupParent,
    string? Runtime,
    bool Provider,
    bool UseApiSocket,
    IReadOnlyList<MountFacts> Mounts,
    IReadOnlyList<int> PublishedPorts,
    IReadOnlyList<string> VolumesFrom,
    IReadOnlyList<string> LabelKeys,
    string? ContainerName,
    IReadOnlyList<string> UnknownKeys);

/// <param name="Type">bind, volume, tmpfs, image, npipe or cluster.</param>
/// <param name="Source">The host path of a bind mount, the key of a named volume; null for an anonymous volume or tmpfs.</param>
public sealed record MountFacts(string Type, string? Source, bool ReadOnly, string? Propagation);

/// <param name="Key">The key under top-level volumes or networks.</param>
/// <param name="Name">The name Docker gets: "&lt;stack&gt;_&lt;key&gt;" unless the file says otherwise.</param>
public sealed record NamedResource(string Key, string Name, bool External, string? Driver, bool HasDriverOptions);

/// <param name="Link">The symbolic link the path goes through; null when the agent could not see the path.</param>
public sealed record InspectedMount(string Path, string? Link);
