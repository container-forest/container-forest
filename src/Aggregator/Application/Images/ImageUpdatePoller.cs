using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Images;

/// <summary>
/// Checks the policies that poll (ADR-0015, item 2) once their interval has passed since their last check: only those not
/// pinned, of stacks whose agent is online. Each check runs in its own scope, and one that fails stops no other. Settings
/// are read on every run, so a change in the panel takes effect without a restart. Singleton.
/// </summary>
public sealed partial class ImageUpdatePoller(
    IServiceScopeFactory scopes,
    IAgentChannel agents,
    TimeProvider time,
    ILogger<ImageUpdatePoller> logger)
{
    /// <returns>How many policies were checked.</returns>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        List<ImagePolicyId> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var settings = ImageUpdateSettings.From(
                await scope.ServiceProvider.GetRequiredService<SettingsStore>().ResolveAsync(ImageUpdateSettingsSection.Instance, cancellationToken));
            if (!settings.PollingEnabled)
            {
                return 0;
            }

            var now = time.GetUtcNow();
            var stacks = scope.ServiceProvider.GetRequiredService<IStackRepository>();
            due = [];
            foreach (var policy in await scope.ServiceProvider.GetRequiredService<IImagePolicyRepository>().ListPolledAsync(cancellationToken))
            {
                // A little early is still due: the timer ticks once a minute, and an interval of five must not become six.
                if (policy.LastCheckedAt is { } last && now - last < settings.PollingInterval - TimeSpan.FromSeconds(30))
                {
                    continue;
                }

                if (await stacks.FindAsync(policy.StackId, cancellationToken) is { } stack && agents.IsOnline(stack.EnvironmentId))
                {
                    due.Add(policy.Id);
                }
            }
        }

        foreach (var id in due)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ImageUpdateService>()
                    .CheckAsync(id, new ImageUpdateRequest(ImageUpdateSource.Poll), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogFailed(exception, id);
            }
        }

        return due.Count;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Polling image policy {PolicyId} failed")]
    private partial void LogFailed(Exception exception, ImagePolicyId policyId);
}
