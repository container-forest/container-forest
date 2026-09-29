namespace ContainerForest.Aggregator.Domain.Stacks;

public enum StackOperation
{
    /// <summary>docker compose restart: restarts processes in the existing containers.</summary>
    Restart,

    /// <summary>docker compose up with --pull missing: applies configuration changes.</summary>
    Redeploy,

    /// <summary>docker compose up with --pull always: updates images.</summary>
    RedeployWithPull,

    /// <summary>docker compose down: removes containers and networks, keeps volumes.</summary>
    Down,

    /// <summary>Removes the stack from the system.</summary>
    Remove,
}
