using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Containers;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Environments;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Metrics;

/// <summary>The periods a graph can show.</summary>
public enum MetricsRange
{
    FifteenMinutes,
    OneHour,
    SixHours,
    OneDay,
    OneWeek,
    OneMonth,
    ThreeMonths,
}

/// <summary>The points of one query, in buckets of <see cref="StepSeconds"/> from <see cref="From"/> (Unix seconds).</summary>
public sealed record MetricsWindow(MetricsResolution Resolution, long From, long To, long StepSeconds);

public sealed record ContainerSeries(string Name, string Stack, string Service, IReadOnlyList<ContainerPoint> Points);

/// <summary>A deployment that finished inside the window, for a marker on the graphs.</summary>
public sealed record DeploymentMarker(DeploymentId Id, DateTimeOffset At, DeploymentStatus Status, string? Commit);

public sealed record ContainerUsage(MetricsWindow Window, ContainerSeries? Series, IReadOnlyList<DeploymentMarker> Deployments);

/// <param name="Volumes">The stack's named volumes from the latest docker system df; empty before the first one.</param>
public sealed record StackMetrics(
    MetricsWindow Window, IReadOnlyList<ContainerSeries> Containers, IReadOnlyList<VolumeUsage> Volumes, IReadOnlyList<DeploymentMarker> Deployments);

/// <param name="Host">Null without a role on the whole environment, or when the agent sends no host numbers.</param>
/// <param name="Stacks">The sums of each visible compose project's containers; "" collects containers outside compose.</param>
public sealed record EnvironmentMetrics(
    MetricsWindow Window,
    MetricsSourceKind Source,
    int IntervalSeconds,
    IReadOnlyList<HostPoint>? Host,
    IReadOnlyList<FilesystemPoint>? Filesystems,
    IReadOnlyList<ContainerSeries> Stacks,
    DiskUsageSample? DiskUsage);

/// <summary>The latest sample of an environment, limited to what the user may see.</summary>
public sealed record CurrentMetrics(
    MetricsSourceKind Source, int IntervalSeconds, DateTimeOffset? At, HostSample? Host, IReadOnlyList<ContainerSample> Containers);

/// <summary>
/// Resource usage for the panel, the CLI and the bot. Visibility is that of containers (ContainerQueryService): a
/// container of a managed stack needs a role on the stack or its environment, any other container and the host need a
/// role on the whole environment, which every public environment gives each user as a Viewer.
/// </summary>
public sealed class MetricsQueryService(
    CurrentActor currentActor,
    IEnvironmentRepository environments,
    IStackRepository stacks,
    IRoleAssignmentRepository roleAssignments,
    IDeploymentRepository deployments,
    IAgentChannel agents,
    IMetricsStore store,
    MetricsBuffer buffer,
    SettingsStore settings,
    TimeProvider time)
{
    /// <summary>About this many points per graph.</summary>
    public const int TargetPoints = 360;

    // One bucket for all of the stored history.
    private const long WholeHistory = 1L << 40;

    private static readonly long[] Steps = [60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400];

    public async Task<CurrentMetrics> GetCurrentAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var scope = await GetScopeAsync(environmentId, cancellationToken);
        var state = buffer.GetState(environmentId);
        var latest = state.Latest;
        return new CurrentMetrics(
            state.Source,
            state.IntervalSeconds,
            latest?.At,
            scope.CanSeeHost ? latest?.Host : null,
            latest?.Containers.Where(c => scope.CanSeeContainer(c.StackName)).OrderBy(c => c.Name, StringComparer.Ordinal).ToList() ?? []);
    }

    public async Task<EnvironmentMetrics> GetEnvironmentAsync(EnvironmentId environmentId, MetricsRange range, CancellationToken cancellationToken)
    {
        var scope = await GetScopeAsync(environmentId, cancellationToken);
        var window = await ChooseWindowAsync(range, cancellationToken);
        var state = buffer.GetState(environmentId);

        IReadOnlyList<HostPoint>? host = null;
        IReadOnlyList<FilesystemPoint>? filesystems = null;
        if (scope.CanSeeHost)
        {
            host = await QueryHostAsync(environmentId, window, cancellationToken);
            filesystems = await QueryFilesystemsAsync(environmentId, window, cancellationToken);
        }

        var containers = (await QueryContainersAsync(environmentId, window, ContainerFilter.All, cancellationToken))
            .Where(p => scope.CanSeeContainer(p.Stack));
        var perStack = containers
            .GroupBy(p => p.Stack, StringComparer.Ordinal)
            .Select(g => new ContainerSeries(g.Key, g.Key, "", SumByBucket(environmentId, g.Key, g)))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        return new EnvironmentMetrics(window, state.Source, state.IntervalSeconds, host, filesystems, perStack, scope.CanSeeHost ? state.DiskUsage : null);
    }

    public async Task<StackMetrics> GetStackAsync(StackId stackId, MetricsRange range, CancellationToken cancellationToken)
    {
        var stack = await stacks.FindAsync(stackId, cancellationToken) ?? throw new NotFoundException("Stack", stackId);
        var scope = await GetScopeAsync(stack.EnvironmentId, cancellationToken);
        if (!scope.CanSeeContainer(stack.Name.Value))
        {
            throw new NotFoundException("Stack", stackId);
        }

        var window = await ChooseWindowAsync(range, cancellationToken);
        var markers = await GetDeploymentsAsync(stack.Id, window, cancellationToken);
        return await GetStackAsync(scope, stack.Name.Value, window, markers, cancellationToken);
    }

    /// <summary>A compose project without a stack record (ADR-0016): governed by the role on the whole environment.</summary>
    public async Task<StackMetrics> GetUnmanagedStackAsync(
        EnvironmentId environmentId, string projectName, MetricsRange range, CancellationToken cancellationToken)
    {
        var scope = await GetScopeAsync(environmentId, cancellationToken);
        if (!StackName.TryParse(projectName, out _) || scope.Managed.ContainsKey(projectName) || !scope.CanSeeHost)
        {
            throw new NotFoundException("Unmanaged stack", $"'{projectName}'");
        }

        return await GetStackAsync(scope, projectName, await ChooseWindowAsync(range, cancellationToken), [], cancellationToken);
    }

    /// <param name="container">Full id, exact name or unique id prefix of a current container, or the name of a gone one.</param>
    public async Task<ContainerUsage> GetContainerAsync(
        EnvironmentId environmentId, string container, MetricsRange range, CancellationToken cancellationToken)
    {
        var scope = await GetScopeAsync(environmentId, cancellationToken);
        var (name, stackName) = await ResolveContainerAsync(environmentId, container, cancellationToken);
        if (!scope.CanSeeContainer(stackName))
        {
            throw new NotFoundException("Container", $"'{container}'");
        }

        var window = await ChooseWindowAsync(range, cancellationToken);
        var points = await QueryContainersAsync(environmentId, window, new ContainerFilter(Container: name), cancellationToken);
        var series = points.Count > 0 ? new ContainerSeries(name, points[^1].Stack, points[^1].Service, points) : null;
        var markers = stackName is not null && scope.Managed.TryGetValue(stackName, out var stack)
            ? await GetDeploymentsAsync(stack.Id, window, cancellationToken)
            : [];
        return new ContainerUsage(window, series, markers);
    }

    private async Task<StackMetrics> GetStackAsync(
        Scope scope, string stackName, MetricsWindow window, IReadOnlyList<DeploymentMarker> markers, CancellationToken cancellationToken)
    {
        var points = await QueryContainersAsync(scope.Environment.Id, window, new ContainerFilter(Stack: stackName), cancellationToken);
        var series = points
            .GroupBy(p => p.Container, StringComparer.Ordinal)
            .Select(g => new ContainerSeries(g.Key, stackName, g.Last().Service, g.ToList()))
            .OrderBy(s => s.Service, StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        var volumes = buffer.GetState(scope.Environment.Id).DiskUsage?.Volumes.Where(v => v.StackName == stackName).OrderBy(v => v.Name, StringComparer.Ordinal).ToList() ?? [];
        return new StackMetrics(window, series, volumes, markers);
    }

    // A container that still exists is found as in the docker CLI; one that is gone (or an offline agent) by its name.
    private async Task<(string Name, string? Stack)> ResolveContainerAsync(EnvironmentId environmentId, string container, CancellationToken cancellationToken)
    {
        try
        {
            var found = ContainerLookup.Find(await agents.ListContainersAsync(environmentId, cancellationToken), container);
            return (found.Name, found.StackName);
        }
        catch (Exception ex) when (ex is NotFoundException or AgentException)
        {
            var known = buffer.GetState(environmentId).Latest?.Containers.FirstOrDefault(c => c.Name == container);
            if (known is not null)
            {
                return (known.Name, known.StackName);
            }

            var to = time.GetUtcNow().ToUnixTimeSeconds() + 1;
            foreach (var resolution in (MetricsResolution[])[MetricsResolution.Minute, MetricsResolution.Hour])
            {
                var stored = await store.QueryContainersAsync(
                    environmentId, resolution, new ContainerFilter(Container: container), 0, to, WholeHistory, cancellationToken);
                if (stored.Count > 0)
                {
                    return (container, stored[^1].Stack is { Length: > 0 } stack ? stack : null);
                }
            }

            throw new NotFoundException("Container", $"'{container}'");
        }
    }

    private async Task<IReadOnlyList<DeploymentMarker>> GetDeploymentsAsync(StackId stackId, MetricsWindow window, CancellationToken cancellationToken)
    {
        var from = DateTimeOffset.FromUnixTimeSeconds(window.From);
        return (await deployments.ListByStackAsync(stackId, 200, cancellationToken))
            .Where(d => d.FinishedAt >= from)
            .Select(d => new DeploymentMarker(d.Id, d.FinishedAt!.Value, d.Status, d.Commit?.Value))
            .OrderBy(d => d.At)
            .ToList();
    }

    /// <summary>Raw samples for an hour or less, minutes while they are kept, hours after that.</summary>
    public async Task<MetricsWindow> ChooseWindowAsync(MetricsRange range, CancellationToken cancellationToken)
    {
        var length = LengthOf(range);
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var from = now - (long)length.TotalSeconds;
        if (length <= MetricsBuffer.RawWindow)
        {
            return new MetricsWindow(MetricsResolution.Raw, from, now + 1, 0);
        }

        var minuteRetention = MetricsSettings.From(await settings.ResolveAsync(MetricsSettingsSection.Instance, cancellationToken)).MinuteRetentionDays;
        var wanted = (long)Math.Ceiling(length.TotalSeconds / TargetPoints);
        var step = Steps.FirstOrDefault(s => s >= wanted, Steps[^1]);
        var resolution = step < 3600 && from >= now - (minuteRetention * 86400L) ? MetricsResolution.Minute : MetricsResolution.Hour;
        if (resolution == MetricsResolution.Hour)
        {
            step = Math.Max(3600, step / 3600 * 3600);
        }

        return new MetricsWindow(resolution, from / step * step, now + 1, step);
    }

    public static TimeSpan LengthOf(MetricsRange range) => range switch
    {
        MetricsRange.FifteenMinutes => TimeSpan.FromMinutes(15),
        MetricsRange.OneHour => TimeSpan.FromHours(1),
        MetricsRange.SixHours => TimeSpan.FromHours(6),
        MetricsRange.OneDay => TimeSpan.FromDays(1),
        MetricsRange.OneWeek => TimeSpan.FromDays(7),
        MetricsRange.OneMonth => TimeSpan.FromDays(30),
        MetricsRange.ThreeMonths => TimeSpan.FromDays(90),
        _ => throw new DomainValidationException("Unknown range."),
    };

    // Raw windows take the minutes before the oldest sample still in memory (after a restart, the whole hour).
    private async Task<IReadOnlyList<ContainerPoint>> QueryContainersAsync(
        EnvironmentId environmentId, MetricsWindow window, ContainerFilter filter, CancellationToken cancellationToken)
    {
        if (window.Resolution != MetricsResolution.Raw)
        {
            return await store.QueryContainersAsync(environmentId, window.Resolution, filter, window.From, window.To, window.StepSeconds, cancellationToken);
        }

        var samples = buffer.GetSamples(environmentId, DateTimeOffset.FromUnixTimeSeconds(window.From));
        var rawFrom = samples.Count > 0 ? samples[0].At.ToUnixTimeSeconds() : window.To;
        var older = rawFrom > window.From
            ? await store.QueryContainersAsync(environmentId, MetricsResolution.Minute, filter, window.From, MetricsBuffer.MinuteOf(DateTimeOffset.FromUnixTimeSeconds(rawFrom)), 60, cancellationToken)
            : [];
        var raw = samples.SelectMany(s => s.Containers
            .Where(c => Matches(filter, c))
            .Select(c => RawPoint(environmentId, s.At, c)));
        return older.Concat(raw).ToList();
    }

    private async Task<IReadOnlyList<HostPoint>> QueryHostAsync(EnvironmentId environmentId, MetricsWindow window, CancellationToken cancellationToken)
    {
        if (window.Resolution != MetricsResolution.Raw)
        {
            return await store.QueryHostAsync(environmentId, window.Resolution, window.From, window.To, window.StepSeconds, cancellationToken);
        }

        var samples = buffer.GetSamples(environmentId, DateTimeOffset.FromUnixTimeSeconds(window.From));
        var rawFrom = samples.Count > 0 ? samples[0].At.ToUnixTimeSeconds() : window.To;
        var older = rawFrom > window.From
            ? await store.QueryHostAsync(environmentId, MetricsResolution.Minute, window.From, MetricsBuffer.MinuteOf(DateTimeOffset.FromUnixTimeSeconds(rawFrom)), 60, cancellationToken)
            : [];
        var raw = samples.Where(s => s.Host is not null).Select(s => RawPoint(environmentId, s.At, s.Host!));
        return older.Concat(raw).ToList();
    }

    // Fullness changes slowly: minutes are fine grained enough even for raw windows.
    private Task<IReadOnlyList<FilesystemPoint>> QueryFilesystemsAsync(EnvironmentId environmentId, MetricsWindow window, CancellationToken cancellationToken) =>
        window.Resolution == MetricsResolution.Raw
            ? store.QueryFilesystemsAsync(environmentId, MetricsResolution.Minute, window.From / 60 * 60, window.To, 60, cancellationToken)
            : store.QueryFilesystemsAsync(environmentId, window.Resolution, window.From, window.To, window.StepSeconds, cancellationToken);

    private static bool Matches(ContainerFilter filter, ContainerSample sample) =>
        (filter.Container is null || filter.Container == sample.Name)
        && (filter.Stack is null || filter.Stack == (sample.StackName ?? ""));

    private static ContainerPoint RawPoint(EnvironmentId environmentId, DateTimeOffset at, ContainerSample c) => new(
        environmentId, c.Name, at.ToUnixTimeSeconds(), c.StackName ?? "", c.ServiceName ?? "",
        c.CpuCores, c.CpuCores, c.CpuLimitCores, c.CpuThrottledRatio,
        c.MemoryWorkingSetBytes, c.MemoryWorkingSetBytes, c.MemoryLimitBytes, c.MemoryCacheBytes,
        c.NetworkRxBytesPerSecond, c.NetworkTxBytesPerSecond, c.BlockReadBytesPerSecond, c.BlockWriteBytesPerSecond, c.Pids);

    private static HostPoint RawPoint(EnvironmentId environmentId, DateTimeOffset at, HostSample h) => new(
        environmentId, at.ToUnixTimeSeconds(), h.CpuCount, h.CpuUser, h.CpuSystem, h.CpuIowait, h.CpuSteal, h.CpuBusy, h.Load1,
        h.MemoryUsedBytes, h.MemoryUsedBytes, h.MemoryTotalBytes, h.SwapUsedBytes, h.SwapTotalBytes,
        h.DiskReadBytesPerSecond, h.DiskWriteBytesPerSecond, h.NetworkRxBytesPerSecond, h.NetworkTxBytesPerSecond);

    // Sums of containers per bucket: a stack's total. Maxima are summed too, an upper bound of the stack's peak.
    private static List<ContainerPoint> SumByBucket(EnvironmentId environmentId, string stack, IEnumerable<ContainerPoint> points) =>
        points.GroupBy(p => p.Start)
            .OrderBy(g => g.Key)
            .Select(g => new ContainerPoint(
                environmentId, stack, g.Key, stack, "",
                g.Sum(p => p.CpuAvg), g.Sum(p => p.CpuMax), g.Sum(p => p.CpuLimit), g.Max(p => p.ThrottledAvg),
                g.Sum(p => p.MemoryAvg), g.Sum(p => p.MemoryMax), g.Sum(p => p.MemoryLimit), g.Sum(p => p.CacheAvg),
                g.Sum(p => p.RxAvg), g.Sum(p => p.TxAvg), g.Sum(p => p.ReadAvg), g.Sum(p => p.WriteAvg), g.Sum(p => p.PidsMax)))
            .ToList();

    private async Task<Scope> GetScopeAsync(EnvironmentId environmentId, CancellationToken cancellationToken)
    {
        var actor = await currentActor.GetAsync(cancellationToken);
        var environment = await environments.FindAsync(environmentId, cancellationToken);
        var assignments = await roleAssignments.ListForUserAsync(actor.Id, cancellationToken);
        if (environment is null || !AccessPolicy.CanSee(actor, environment, assignments))
        {
            throw new NotFoundException("Environment", environmentId);
        }

        var managed = (await stacks.ListByEnvironmentAsync(environment.Id, cancellationToken)).ToDictionary(s => s.Name.Value, StringComparer.Ordinal);
        return new Scope(actor, environment, assignments, managed);
    }

    private sealed record Scope(Actor Actor, DockerEnvironment Environment, IReadOnlyList<RoleAssignment> Assignments, IReadOnlyDictionary<string, Stack> Managed)
    {
        public bool CanSeeHost { get; } = AccessPolicy.CanPerform(Actor, Environment, Assignments, Permission.View);

        public bool CanSeeContainer(string? stackName) =>
            stackName is { Length: > 0 } name && Managed.TryGetValue(name, out var stack)
                ? AccessPolicy.CanSee(Actor, Environment, stack, Assignments)
                : CanSeeHost;
    }
}
