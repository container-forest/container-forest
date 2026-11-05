using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Agents;

/// <summary>
/// Sends commands to connected agents. Implemented over the gRPC stream in the infrastructure layer.
/// </summary>
/// <remarks>
/// Commands are never queued: when the agent is offline the call fails at once with
/// <see cref="AgentOfflineException"/> (security invariant 7).
/// </remarks>
public interface IAgentChannel
{
    bool IsOnline(EnvironmentId environmentId);

    /// <summary>
    /// Closes the agent's live connection, if there is one; its waiting commands fail with
    /// <see cref="AgentDisconnectedException"/>. Used after revoking its credentials, which a live connection would outlast.
    /// </summary>
    void Disconnect(EnvironmentId environmentId, string reason);

    Task<IReadOnlyList<ContainerSnapshot>> ListContainersAsync(EnvironmentId environmentId, CancellationToken cancellationToken);

    Task StartContainerAsync(EnvironmentId environmentId, string containerId, CancellationToken cancellationToken);

    /// <param name="stopTimeout">How long Docker waits for the process to exit before killing it.</param>
    Task StopContainerAsync(
        EnvironmentId environmentId, string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken);

    /// <param name="stopTimeout">How long Docker waits for the process to exit before killing it.</param>
    Task RestartContainerAsync(
        EnvironmentId environmentId, string containerId, TimeSpan stopTimeout, CancellationToken cancellationToken);

    /// <summary>Writes the stack files on the host and runs docker compose up; returns when compose has finished.</summary>
    /// <param name="onProgress">
    /// Receives each line of compose output as it arrives. Called from the connection's receive loop:
    /// it must be quick and thread-safe.
    /// </param>
    /// <param name="timeout">How long to wait for the result; pulling images can take minutes.</param>
    Task DeployStackAsync(
        EnvironmentId environmentId,
        StackBundle bundle,
        Action<string> onProgress,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Has the agent render the bundle with docker compose config, for the compose policy (ADR-0012); nothing on the
    /// host changes. Lines compose prints on stderr go to <paramref name="onProgress"/>.
    /// </summary>
    /// <exception cref="AgentCommandFailedException">With <see cref="AgentErrorKind.Unsupported"/> for an agent too old to render.</exception>
    Task<Policies.RenderedCompose> RenderStackAsync(
        EnvironmentId environmentId, StackBundle bundle, Action<string> onProgress, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Runs docker compose restart for a deployed stack; returns when compose has finished.</summary>
    /// <param name="stopTimeout">How long Docker waits for each container's process to exit before killing it.</param>
    /// <param name="onProgress">Receives each line of compose output, as in <see cref="DeployStackAsync"/>.</param>
    Task RestartStackAsync(
        EnvironmentId environmentId,
        StackName stackName,
        TimeSpan stopTimeout,
        Action<string> onProgress,
        CancellationToken cancellationToken);

    /// <summary>Runs docker compose down for a deployed stack: containers and networks go, volumes and files stay.</summary>
    /// <param name="stopTimeout">How long Docker waits for each container's process to exit before killing it.</param>
    /// <param name="onProgress">Receives each line of compose output, as in <see cref="DeployStackAsync"/>.</param>
    Task DownStackAsync(
        EnvironmentId environmentId,
        StackName stackName,
        TimeSpan stopTimeout,
        Action<string> onProgress,
        CancellationToken cancellationToken);

    /// <summary>Reads the end of a container's log without following it; returns once the agent has sent all of it.</summary>
    /// <param name="containerId">The container's full id.</param>
    /// <returns>Oldest first, at most <see cref="ContainerLogQuery.TailLines"/> lines.</returns>
    Task<IReadOnlyList<ContainerLogLine>> ReadContainerLogsAsync(
        EnvironmentId environmentId, string containerId, ContainerLogQuery query, CancellationToken cancellationToken);
}

/// <summary>Everything the agent needs to deploy one stack. It may hold decrypted secrets, so it is never logged.</summary>
/// <param name="Files">Paths are relative to the stack directory; the compose file is always <see cref="ComposeFileName"/>.</param>
public sealed record StackBundle(StackName StackName, CommitSha Commit, IReadOnlyList<BundleFile> Files, ImagePullPolicy Pull)
{
    /// <summary>Name of the compose file in the stack directory on the host; the agent passes it to compose with -f.</summary>
    public const string ComposeFileName = "compose.yaml";

    /// <summary>Logins the agent uses to pull the stack's images; set by the registry credentials step.</summary>
    public IReadOnlyList<RegistryLogin> RegistryLogins { get; init; } = [];

    /// <summary>The agent restarts the stack after compose up when its other files changed (<see cref="Stack.RestartOnFileChange"/>).</summary>
    public bool RestartOnFileChange { get; init; }

    /// <summary>Compose reads variables only from the bundle's .env, as when it was rendered (ADR-0012).</summary>
    public bool ExplicitEnvFile { get; init; }

    /// <summary>Host paths the agent checks for symbolic links right before compose up (ADR-0012).</summary>
    public IReadOnlyList<string> VerifyPaths { get; init; } = [];

    public long TotalBytes => Files.Sum(f => (long)f.Content.Length);
}

/// <summary>A decrypted registry login for the agent.</summary>
/// <param name="Server">The key in docker's config.json, e.g. "ghcr.io" or "https://index.docker.io/v1/".</param>
public sealed record RegistryLogin(string Server, string Username, string Password)
{
    // Records print every property; the password must not reach a log through ToString.
    public override string ToString() => $"{nameof(RegistryLogin)} {{ Server = {Server}, Username = {Username} }}";
}

/// <param name="Mode">Unix permission bits, e.g. 0o644.</param>
/// <param name="IsSecret">Decrypted from SOPS: the agent writes it to tmpfs and links it into the stack directory.</param>
public sealed record BundleFile(StackFilePath Path, byte[] Content, int Mode, bool IsSecret = false)
{
    public const int RegularMode = 0b110_100_100;     // 0644
    public const int ExecutableMode = 0b111_101_101;  // 0755
}

/// <summary>Live state of a container as the agent reports it; not stored in the database.</summary>
/// <param name="IsSystem">
/// Labelled as a part of container-forest itself (the aggregator, the panel's Caddy, an agent) in its compose file.
/// Such containers are deployed by hand, not as managed stacks, so the label is how the aggregator knows them.
/// </param>
public sealed record ContainerSnapshot(
    string Id,
    string Name,
    ContainerState State,
    string Image,
    string? StackName,
    string? ServiceName,
    string StatusText,
    DateTimeOffset? CreatedAt,
    bool IsSystem = false);

public enum ContainerState
{
    Unknown,
    Created,
    Running,
    Paused,
    Restarting,
    Removing,
    Exited,
    Dead,
}

/// <summary>Which part of a container's log to read.</summary>
/// <param name="TailLines">How many of the last lines, at least one.</param>
/// <param name="Since">Only lines written at or after this moment; null for no lower bound.</param>
public sealed record ContainerLogQuery(int TailLines, DateTimeOffset? Since);

/// <summary>One line of a container's log. It may hold secrets the application printed itself.</summary>
/// <param name="At">When Docker received the line; null when the agent did not report it.</param>
public sealed record ContainerLogLine(DateTimeOffset? At, LogStream Stream, string Text);

public enum LogStream
{
    Stdout,
    Stderr,
}
