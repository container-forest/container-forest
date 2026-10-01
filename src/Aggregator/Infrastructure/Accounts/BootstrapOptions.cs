namespace ContainerForest.Aggregator.Infrastructure.Accounts;

/// <summary>
/// Configuration section "Bootstrap" (env: CF_BOOTSTRAP__...): the first administrator, created when the
/// database has no users. Set both values or neither.
/// </summary>
public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    public string? AdminUserName { get; set; }

    public string? AdminPassword { get; set; }

    internal bool IsConfigured => !string.IsNullOrEmpty(AdminUserName) && !string.IsNullOrEmpty(AdminPassword);

    internal bool IsValid() => string.IsNullOrEmpty(AdminUserName) == string.IsNullOrEmpty(AdminPassword);
}
