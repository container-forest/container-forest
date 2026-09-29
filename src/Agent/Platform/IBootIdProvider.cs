namespace ContainerForest.Agent.Platform;

/// <summary>
/// Identifies the current boot of the host. A new value in Hello tells the aggregator the host rebooted,
/// so file secrets on tmpfs are gone and must be sent again.
/// </summary>
public interface IBootIdProvider
{
    string GetBootId();
}
