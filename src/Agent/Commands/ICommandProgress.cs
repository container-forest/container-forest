namespace ContainerForest.Agent.Commands;

/// <summary>Streams output of a running command (e.g. `docker compose` lines) to the aggregator.</summary>
/// <remarks>Implementations must accept concurrent calls: compose output is read from stdout and stderr at the same time.</remarks>
public interface ICommandProgress
{
    Task ReportAsync(string line, CancellationToken cancellationToken);
}
