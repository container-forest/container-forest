namespace ContainerForest.Agent.Commands;

/// <summary>Streams output of a running command (e.g. `docker compose` lines) to the aggregator.</summary>
public interface ICommandProgress
{
    Task ReportAsync(string line, CancellationToken cancellationToken);
}
