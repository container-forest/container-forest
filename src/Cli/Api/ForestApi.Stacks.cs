using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Api;

/// <summary>What can be done to a deployed stack as a whole without deploying it.</summary>
public enum StackAction
{
    Restart,
    Down,
}

public sealed partial class ForestApi
{
    public async Task<IReadOnlyList<StackResponse>> ListStacksAsync(Guid environmentId, CancellationToken cancellationToken) =>
        await Api.Environments[environmentId].Stacks.GetAsync(cancellationToken: cancellationToken) ?? [];

    /// <summary>A stack of the environment by name, or any visible stack by id.</summary>
    /// <exception cref="CliException">There is no such stack the user can see.</exception>
    public async Task<StackResponse> FindStackAsync(EnvironmentResponse environment, string nameOrId, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(nameOrId, out var id))
        {
            return Required(await Api.Stacks[id].GetAsync(cancellationToken: cancellationToken), "stack");
        }

        var stacks = await ListStacksAsync(Required(environment.Id, "environment id"), cancellationToken);
        return stacks.FirstOrDefault(s => string.Equals(s.Name, nameOrId, StringComparison.Ordinal))
            ?? throw new CliException($"There is no stack '{nameOrId}' in {environment.Name} you can see; see 'forest stack list'.", ExitCodes.Denied);
    }

    public async Task<StackResponse> CreateStackAsync(
        Guid environmentId, string name, Guid repositoryId, string branch, string composePath, CancellationToken cancellationToken) =>
        Required(
            await Api.Environments[environmentId].Stacks.PostAsync(
                new CreateStackRequest { Name = name, RepositoryId = repositoryId, Branch = branch, ComposePath = composePath },
                cancellationToken: cancellationToken),
            "created stack");

    public async Task<StackResponse> ChangeStackSourceAsync(
        Guid stackId, Guid repositoryId, string branch, string composePath, CancellationToken cancellationToken) =>
        Required(
            await Api.Stacks[stackId].Source.PutAsync(
                new ChangeStackSourceRequest { RepositoryId = repositoryId, Branch = branch, ComposePath = composePath },
                cancellationToken: cancellationToken),
            "stack");

    public Task RemoveStackAsync(Guid stackId, CancellationToken cancellationToken) =>
        Api.Stacks[stackId].DeleteAsync(cancellationToken: cancellationToken);

    /// <summary>Queues a deployment; with <paramref name="pull"/> every image is pulled again (redeploy with pull).</summary>
    public async Task<DeploymentDetailsResponse> DeployAsync(Guid stackId, bool pull, CancellationToken cancellationToken) =>
        Required(
            await Api.Stacks[stackId].Deployments.PostAsync(new DeployRequest { Pull = pull }, cancellationToken: cancellationToken),
            "queued deployment");

    /// <summary>Runs compose restart or down and returns its output; waits as long as compose does.</summary>
    public async Task<StackCommandResponse> ControlStackAsync(
        Guid stackId, StackAction action, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        var stack = Api.Stacks[stackId];
        var response = action switch
        {
            StackAction.Restart => await stack.Restart.PostAsync(c => c.QueryParameters.TimeoutSeconds = timeoutSeconds, cancellationToken),
            StackAction.Down => await stack.Down.PostAsync(c => c.QueryParameters.TimeoutSeconds = timeoutSeconds, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
        return Required(response, "compose output");
    }

    public async Task<StackResponse> SetDeployOnPushAsync(Guid stackId, bool enabled, CancellationToken cancellationToken) =>
        Required(
            await Api.Stacks[stackId].DeployOnPush.PutAsync(new DeployOnPushRequest { Enabled = enabled }, cancellationToken: cancellationToken),
            "stack");

    public async Task<StackResponse> SetRestartOnFileChangeAsync(Guid stackId, bool enabled, CancellationToken cancellationToken) =>
        Required(
            await Api.Stacks[stackId].RestartOnFileChange.PutAsync(
                new RestartOnFileChangeRequest { Enabled = enabled }, cancellationToken: cancellationToken),
            "stack");

    public async Task<DeployHookTokenResponse> CreateDeployHookAsync(Guid stackId, CancellationToken cancellationToken) =>
        Required(await Api.Stacks[stackId].DeployHook.PostAsync(cancellationToken: cancellationToken), "deploy hook");

    public async Task RemoveDeployHookAsync(Guid stackId, CancellationToken cancellationToken) =>
        await Api.Stacks[stackId].DeployHook.DeleteAsync(cancellationToken: cancellationToken);

    /// <summary>The stack's deployments, newest first.</summary>
    public async Task<IReadOnlyList<DeploymentResponse>> ListDeploymentsAsync(Guid stackId, CancellationToken cancellationToken) =>
        await Api.Stacks[stackId].Deployments.GetAsync(cancellationToken: cancellationToken) ?? [];

    public async Task<DeploymentDetailsResponse> GetDeploymentAsync(Guid deploymentId, CancellationToken cancellationToken) =>
        Required(await Api.Deployments[deploymentId].GetAsync(cancellationToken: cancellationToken), "deployment");
}
