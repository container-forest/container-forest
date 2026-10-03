using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Deployments.Steps;

/// <summary>
/// Turns the fetched files into the bundle for the agent. The compose file is renamed to
/// <see cref="StackBundle.ComposeFileName"/>, so the agent always knows which file to pass to compose.
/// </summary>
public sealed class BuildBundleStep(DeploymentOptions options) : IDeployStep
{
    public string Name => "Build bundle";

    public Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
    {
        var composeName = context.Stack.ComposePath.FileName;
        var bundlePath = StackFilePath.Parse(StackBundle.ComposeFileName);

        var files = new List<BundleFile>();
        foreach (var file in context.Source.Files)
        {
            var isCompose = file.Path.Value == composeName;
            if (!isCompose && file.Path == bundlePath)
            {
                throw new DeploymentFailedException(
                    $"The stack directory contains '{StackBundle.ComposeFileName}' next to the stack's compose file " +
                    $"'{composeName}'; the agent stores the compose file under that name. Rename or move one of them.");
            }

            var mode = file.IsExecutable ? BundleFile.ExecutableMode : BundleFile.RegularMode;
            files.Add(new BundleFile(isCompose ? bundlePath : file.Path, file.Content, mode, file.IsSecret));
        }

        var bundle = new StackBundle(context.Stack.Name, context.Deployment.Commit!, files, context.Deployment.PullPolicy);
        if (bundle.TotalBytes > options.MaxBundleBytes)
        {
            throw new DeploymentFailedException(
                $"Stack files take {Kib(bundle.TotalBytes)}, more than the limit of {Kib(options.MaxBundleBytes)}. " +
                $"Keep only the stack's files in the directory of its compose file.");
        }

        context.Bundle = bundle;
        context.Info($"Bundle: {files.Count} file(s), {Kib(bundle.TotalBytes)}.");
        return Task.CompletedTask;
    }

    private static string Kib(long bytes) => $"{Math.Ceiling(bytes / 1024.0):0} KiB";
}
