using ContainerForest.Aggregator.Application.Live;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ContainerForest.Aggregator.Infrastructure.Persistence;

/// <summary>
/// What a save changed that open panels show: collected from the change tracker before the save (afterwards every
/// entry is unchanged) and published after it succeeds, so a panel never reloads data that was not written.
/// </summary>
internal sealed class LiveChanges
{
    private readonly Dictionary<StackId, EnvironmentId?> stacks = [];
    private readonly Dictionary<DeploymentId, StackId> deployments = [];
    private bool environments;

    public bool IsEmpty => !environments && stacks.Count == 0 && deployments.Count == 0;

    public static LiveChanges Collect(ChangeTracker tracker)
    {
        var changes = new LiveChanges();
        foreach (var entry in tracker.Entries())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            switch (entry.Entity)
            {
                // Roles decide which environments a user sees.
                case DockerEnvironment or RoleAssignment:
                    changes.environments = true;
                    break;
                case Stack stack:
                    changes.stacks[stack.Id] = stack.EnvironmentId;
                    break;
                case Deployment deployment:
                    changes.deployments[deployment.Id] = deployment.StackId;
                    break;
                // A step that only appends to the log leaves the deployment itself unchanged.
                case DeploymentLogLine when entry.Property("DeploymentId").CurrentValue is DeploymentId owner:
                    var owning = tracker.Entries<Deployment>().FirstOrDefault(d => d.Entity.Id == owner);
                    if (owning is not null)
                    {
                        changes.deployments[owner] = owning.Entity.StackId;
                    }

                    break;
            }
        }

        // The environment of a deployment's stack is usually tracked in the same context (the pipeline loads it).
        foreach (var stack in tracker.Entries<Stack>().Select(e => e.Entity))
        {
            if (changes.deployments.ContainsValue(stack.Id))
            {
                changes.stacks.TryAdd(stack.Id, null);
                changes.stacks[stack.Id] ??= stack.EnvironmentId;
            }
        }

        return changes;
    }

    public async Task PublishAsync(ILiveUpdates live, IQueryable<Stack> stackSet)
    {
        if (environments)
        {
            await live.EnvironmentsChangedAsync();
        }

        foreach (var (deploymentId, stackId) in deployments)
        {
            if (await EnvironmentOfAsync(stackId, stackSet) is { } environmentId)
            {
                await live.DeploymentChangedAsync(environmentId, stackId, deploymentId);
            }
        }

        foreach (var (stackId, environmentId) in stacks)
        {
            // Deployments already tell about their stacks; a stack only seen with them is not repeated.
            if (environmentId is { } known && !deployments.ContainsValue(stackId))
            {
                await live.StackChangedAsync(known, stackId);
            }
        }
    }

    private async Task<EnvironmentId?> EnvironmentOfAsync(StackId stackId, IQueryable<Stack> stackSet)
    {
        if (stacks.TryGetValue(stackId, out var known) && known is not null)
        {
            return known;
        }

        var environmentId = await stackSet.AsNoTracking().Where(s => s.Id == stackId).Select(s => (EnvironmentId?)s.EnvironmentId).FirstOrDefaultAsync();
        stacks[stackId] = environmentId;
        return environmentId;
    }
}
