namespace ContainerForest.Aggregator.Application.Audit;

/// <summary>Configuration section "Audit" (env: CF_AUDIT__...).</summary>
public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>
    /// How many days entries are kept; older ones are removed once a day. 0 keeps them forever. A year covers the
    /// questions a log answers ("who changed this, and when") without the table growing with every sign-in for ever.
    /// </summary>
    public int RetentionDays { get; set; } = 365;

    public bool IsValid() => RetentionDays >= 0;
}
