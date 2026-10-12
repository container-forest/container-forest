using System.Text.RegularExpressions;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Notifications;

/// <summary>A Docker event of a container as the agent reported it (agent.proto, ContainerEvent).</summary>
/// <param name="Action">Docker's action: "die", "stop", "health_status: unhealthy", ...</param>
/// <param name="ContainerName">Null when the container no longer existed when the agent looked it up.</param>
/// <param name="StatusText">Docker's status after the event, e.g. "Exited (1) 2 seconds ago"; null like the name.</param>
public sealed record ContainerObservation(
    EnvironmentId EnvironmentId,
    string ContainerId,
    string Action,
    DateTimeOffset OccurredAt,
    string? ContainerName,
    string? StackName,
    string? ServiceName,
    string? StatusText)
{
    /// <summary>The name for messages: Docker's name, or the short id of a container that is gone.</summary>
    public string DisplayName =>
        !string.IsNullOrEmpty(ContainerName) ? ContainerName : ContainerId[..Math.Min(12, ContainerId.Length)];

    /// <summary>
    /// The exit code of a "die" as the agent read it from the Docker event; null for other actions and from older
    /// agents, which did not send it.
    /// </summary>
    public int? ReportedExitCode { get; init; }

    /// <summary>
    /// <see cref="ReportedExitCode"/>, or else the exit code in Docker's status: 137 of "Exited (137) 1 second ago", or
    /// 1 of "Restarting (1) …" for a container its restart policy is about to start again; null if neither has it,
    /// e.g. an older agent's report of a container that is already "Up" again.
    /// </summary>
    public int? ExitCode => ReportedExitCode ?? StatusExitCode;

    private int? StatusExitCode =>
        StatusText is not null && ContainerIncidentNotifier.ExitedStatus().Match(StatusText) is { Success: true } match
        && int.TryParse(match.Groups[1].ValueSpan, out var code)
            ? code
            : null;

    /// <summary>
    /// Set by <see cref="ContainerIncidentWatch"/> on a "die" that came with the container's "oom": the kernel killed it
    /// for exceeding its memory limit. Docker's event itself says nothing about it.
    /// </summary>
    public bool OutOfMemory { get; init; }

    /// <summary>The container carries the label com.container-forest.system (the agent's <c>ContainerInfo.system</c>).</summary>
    public bool IsSystem { get; init; }
}

/// <summary>
/// Turns a container that died or became unhealthy into a notification. Which events count is decided by
/// <see cref="ContainerIncidentWatch"/>.
/// </summary>
public sealed partial class ContainerIncidentNotifier(
    NotificationDispatcher dispatcher,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    ILogger<ContainerIncidentNotifier> logger)
{
    /// <param name="kind"><see cref="NotificationEventKinds.ContainerDied"/> or <see cref="NotificationEventKinds.ContainerUnhealthy"/>.</param>
    /// <returns>The notifications created; empty when no rule matches or the environment no longer exists.</returns>
    public async Task<IReadOnlyList<Notification>> NotifyAsync(
        ContainerObservation observation, NotificationEventKinds kind, CancellationToken cancellationToken)
    {
        if (kind is not (NotificationEventKinds.ContainerDied or NotificationEventKinds.ContainerUnhealthy))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only container events are notified here.");
        }

        var environment = await environments.FindAsync(observation.EnvironmentId, cancellationToken);
        if (environment is null)
        {
            return [];
        }

        // A compose project is a managed stack only when a stack of that name exists in the environment;
        // otherwise the event is the environment's, and stack rules do not hear about it.
        StackName.TryParse(observation.StackName, out var stackName);
        var stack = stackName is null ? null : await stacks.FindByNameAsync(environment.Id, stackName, cancellationToken);

        var details = new ContainerNotificationDetails(
            observation.ContainerId,
            observation.DisplayName,
            stackName,
            string.IsNullOrEmpty(observation.ServiceName) ? null : observation.ServiceName,
            kind == NotificationEventKinds.ContainerDied ? observation.ExitCode : null,
            kind == NotificationEventKinds.ContainerDied && observation.OutOfMemory,
            observation.IsSystem || stack?.IsSystem == true);
        var notificationEvent = new NotificationEvent(kind, environment.Id, stack?.Id, observation.OccurredAt, details);

        var created = await dispatcher.DispatchAsync(notificationEvent, Summarize(kind, details, environment.Name), cancellationToken);
        if (created.Count > 0)
        {
            logger.LogInformation(
                "{Kind} of container {ContainerId} in environment {EnvironmentId} queued {Count} notification(s)",
                kind, observation.ContainerId, environment.Id, created.Count);
        }

        return created;
    }

    /// <summary>The line the history screen and the Discord message show.</summary>
    public static string Summarize(NotificationEventKinds kind, ContainerNotificationDetails details, string environmentName)
    {
        var stack = details.StackName is null ? "" : $" of stack '{details.StackName}'";
        var target = $"Container '{details.ContainerName}'{stack} on '{environmentName}'";
        return kind switch
        {
            NotificationEventKinds.ContainerDied when details.OutOfMemory => $"{target} was killed: out of memory.",
            NotificationEventKinds.ContainerDied when details.ExitCode is { } code => SignalName(code) is { } signal
                ? $"{target} exited with code {code} ({signal})."
                : $"{target} exited with code {code}.",
            NotificationEventKinds.ContainerDied => $"{target} stopped unexpectedly.",
            NotificationEventKinds.ContainerUnhealthy => $"{target} is unhealthy.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only container events are summarized here."),
        };
    }

    /// <summary>
    /// The signal behind an exit code above 128: a process killed by signal N exits with 128 + N, so 139 is a segfault
    /// and 143 a SIGTERM the process did not handle. Null for other codes and for signals that rarely end a container.
    /// </summary>
    public static string? SignalName(int exitCode) => exitCode switch
    {
        129 => "SIGHUP",
        130 => "SIGINT",
        131 => "SIGQUIT",
        132 => "SIGILL",
        133 => "SIGTRAP",
        134 => "SIGABRT",
        135 => "SIGBUS",
        136 => "SIGFPE",
        137 => "SIGKILL",
        139 => "SIGSEGV",
        141 => "SIGPIPE",
        142 => "SIGALRM",
        143 => "SIGTERM",
        _ => null,
    };

    [GeneratedRegex(@"^(?:Exited|Restarting) \((-?\d+)\)", RegexOptions.CultureInvariant)]
    internal static partial Regex ExitedStatus();
}
