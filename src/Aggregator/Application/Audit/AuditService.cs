using ContainerForest.Aggregator.Application.Access;

namespace ContainerForest.Aggregator.Application.Audit;

/// <summary>Reading the audit log: administrators only, with no limits on their credentials.</summary>
public sealed class AuditService(CurrentActor currentActor, IAuditLog log)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    /// <param name="page">From 1.</param>
    public async Task<AuditPage> ListAsync(AuditFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var skip = (Math.Max(page, 1) - 1) * size;
        return await log.ListAsync(filter with { Search = Normalize(filter.Search) }, skip, size, cancellationToken);
    }

    private static string? Normalize(string? search) =>
        string.IsNullOrWhiteSpace(search) ? null : search.Trim();
}
