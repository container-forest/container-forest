using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Metrics;

/// <summary>Which containers a query covers.</summary>
/// <param name="Container">One container by name.</param>
/// <param name="Stack">The containers of one compose project.</param>
public sealed record ContainerFilter(string? Container = null, string? Stack = null)
{
    public static readonly ContainerFilter All = new();
}

/// <summary>
/// Stored minute and hour points, in a database of their own (metrics.db, ADR-0017): it is written every minute, is
/// not part of the backup and may be deleted without losing anything but history.
/// </summary>
public interface IMetricsStore
{
    Task AppendAsync(IReadOnlyCollection<MinuteBatch> batches, CancellationToken cancellationToken);

    /// <summary>
    /// Points in [<paramref name="from"/>, <paramref name="to"/>) grouped into buckets of <paramref name="stepSeconds"/>,
    /// per container; each bucket's start is a multiple of the step.
    /// </summary>
    Task<IReadOnlyList<ContainerPoint>> QueryContainersAsync(
        EnvironmentId environmentId, MetricsResolution resolution, ContainerFilter filter, long from, long to, long stepSeconds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<HostPoint>> QueryHostAsync(
        EnvironmentId environmentId, MetricsResolution resolution, long from, long to, long stepSeconds, CancellationToken cancellationToken);

    Task<IReadOnlyList<FilesystemPoint>> QueryFilesystemsAsync(
        EnvironmentId environmentId, MetricsResolution resolution, long from, long to, long stepSeconds, CancellationToken cancellationToken);

    /// <summary>Builds hour points from the minutes of [<paramref name="from"/>, <paramref name="to"/>); safe to repeat.</summary>
    Task RollUpHoursAsync(long from, long to, CancellationToken cancellationToken);

    /// <summary>Removes minutes before <paramref name="minutesBefore"/>, hours before <paramref name="hoursBefore"/>,
    /// and every point of environments not in <paramref name="environments"/>. Returns the number of rows removed.</summary>
    Task<int> RemoveExpiredAsync(
        long minutesBefore, long hoursBefore, IReadOnlyCollection<EnvironmentId> environments, CancellationToken cancellationToken);
}
