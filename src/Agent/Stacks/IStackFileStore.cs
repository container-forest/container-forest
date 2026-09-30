namespace ContainerForest.Agent.Stacks;

/// <summary>Stack files on the host: the stack directory and the stack's secret files on tmpfs.</summary>
public interface IStackFileStore
{
    /// <summary>Whether the stack has been deployed on this host, i.e. its directory exists.</summary>
    bool Exists(string stackName);

    /// <summary>
    /// Replaces the stack's files with <paramref name="layout"/>, leaving files the containers created alone.
    /// A bundle that conflicts with the host is rejected before anything changes; after that every file is replaced
    /// atomically. The previous version is kept for a rollback.
    /// </summary>
    /// <returns>The stack directory, which compose runs in; its path never changes between deployments.</returns>
    /// <exception cref="Commands.StackFileConflictException">A path is a directory or goes through a symbolic link on the host.</exception>
    Task<string> WriteAsync(StackLayout layout, CancellationToken cancellationToken);

    /// <summary>Removes the previous version of the stack's secrets once compose runs with the new ones.</summary>
    void DiscardPreviousSecrets(string stackName);

    /// <summary>Removes all secret files of the stack, e.g. after its containers are removed.</summary>
    void DeleteSecrets(string stackName);
}
