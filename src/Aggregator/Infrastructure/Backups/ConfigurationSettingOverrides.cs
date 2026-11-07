using ContainerForest.Aggregator.Application.Settings;
using Microsoft.Extensions.Configuration;

namespace ContainerForest.Aggregator.Infrastructure.Backups;

/// <summary>Settings from the host's configuration: in production, CF_&lt;SECTION&gt;__&lt;FIELD&gt; in the container's environment.</summary>
internal sealed class ConfigurationSettingOverrides(IConfiguration configuration) : ISettingOverrides
{
    public string? Find(string section, string field) =>
        configuration[$"{section}:{field}"] is { Length: > 0 } value ? value : null;
}
