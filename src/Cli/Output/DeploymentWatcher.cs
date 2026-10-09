using ContainerForest.Cli.Api;
using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Output;

/// <summary>Follows a deployment until it finishes, printing its log as it grows.</summary>
public sealed class DeploymentWatcher(ForestApi api, CliOutput output, TimeProvider time)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    public static bool IsFinished(DeploymentStatus? status) =>
        status is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Cancelled;

    /// <summary>Prints the log from the start and returns the finished deployment.</summary>
    public async Task<DeploymentDetailsResponse> WatchAsync(Guid deploymentId, bool follow, CancellationToken cancellationToken)
    {
        var printed = 0;
        while (true)
        {
            var deployment = await api.GetDeploymentAsync(deploymentId, cancellationToken);
            var log = deployment.Log ?? [];
            foreach (var line in log.Skip(printed))
            {
                await WriteAsync(line);
            }

            printed = Math.Max(printed, log.Count);
            if (!follow || IsFinished(deployment.Status))
            {
                await output.Out.FlushAsync(cancellationToken);
                return deployment;
            }

            await Task.Delay(Interval, time, cancellationToken);
        }
    }

    private Task WriteAsync(DeploymentLogLineResponse line) => line.Level switch
    {
        DeploymentLogLevel.Warning => output.Out.WriteLineAsync($"warning: {line.Text}"),
        DeploymentLogLevel.Error => output.Out.WriteLineAsync($"error: {line.Text}"),
        _ => output.Out.WriteLineAsync(line.Text),
    };
}
