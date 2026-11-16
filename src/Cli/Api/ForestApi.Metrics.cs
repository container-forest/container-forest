using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Api;

public sealed partial class ForestApi
{
    /// <summary>The environment's latest resource usage sample, limited to what the user may see (ADR-0017).</summary>
    public async Task<CurrentMetricsResponse> GetCurrentMetricsAsync(Guid environmentId, CancellationToken cancellationToken) =>
        await Api.Environments[environmentId].Metrics.Current.GetAsync(cancellationToken: cancellationToken)
            ?? throw new CliException("The server returned no resource usage.");
}
