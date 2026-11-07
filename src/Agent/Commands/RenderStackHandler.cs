using System.Text.Json;
using ContainerForest.Agent.Compose;
using ContainerForest.Agent.Configuration;
using ContainerForest.Agent.Stacks;
using ContainerForest.Contracts.Agent;
using Microsoft.Extensions.Options;

namespace ContainerForest.Agent.Commands;

/// <summary>
/// Renders a bundle with `docker compose config` for the aggregator's compose policy (ADR-0012). The files go to a
/// throwaway directory on tmpfs, never to the stack directory, and compose reads them with the same variables and
/// --env-file as a deployment with <see cref="DeployStack.ExplicitEnvFile"/>: what the aggregator checks is what
/// compose would run. The agent decides nothing; the result is the model and what it found on the mount sources.
/// </summary>
public sealed class RenderStackHandler(
    IComposeRunner compose,
    HostPathInspector inspector,
    IOptions<AgentOptions> options,
    ILogger<RenderStackHandler> logger)
    : AgentCommandHandler<RenderStack>(logger)
{
    /// <summary>Far above any real stack, and far below gRPC's 4 MB message limit.</summary>
    public const int MaxModelBytes = 1024 * 1024;

    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    protected override async Task<CommandResult> ExecuteAsync(RenderStack command, ICommandProgress progress, CancellationToken cancellationToken)
    {
        var layout = StackLayout.Create(command.StackName, command.Files);
        var environment = DeployStackHandler.ValidateEnvironment(command.Env);
        var directory = Path.Combine(RenderRoot(), Guid.NewGuid().ToString("N"));
        string json;
        try
        {
            await WriteAsync(layout, directory, cancellationToken);
            var envFile = ComposeInvocation.EnvFileFor(directory, HasEnvFile(layout));
            json = await compose.CaptureAsync(
                ComposeInvocation.Config(layout.StackName, directory, environment, envFile), progress, MaxModelBytes, cancellationToken);
        }
        finally
        {
            // The directory holds decrypted secrets: it must not outlive the command, whatever happened.
            TryDelete(directory);
        }

        RenderedModel model;
        try
        {
            model = RenderedModel.Parse(json);
        }
        catch (JsonException)
        {
            throw new DockerCommandFailedException("docker compose config did not print a JSON model; is the compose plugin too old?");
        }

        var stackDirectory = Path.Combine(options.Value.StacksRoot, layout.StackName);
        model.MoveDirectory(directory, stackDirectory).WithoutSecrets();
        var rendered = new RenderedStack
        {
            ModelJson = model.ToJson(),
            StackDirectory = stackDirectory,
            AgentDirectories = { options.Value.DataDirectory, options.Value.StacksRoot, options.Value.SecretsDirectory },
        };
        var secretLinks = inspector.SecretLinks(layout);
        foreach (var source in model.MountSources())
        {
            if (inspector.Inspect(source, secretLinks) is { } problem)
            {
                rendered.InspectedPaths.Add(new InspectedPath { Path = problem.Path, Link = problem.Link ?? "", Unchecked = problem.Link is null });
            }
        }

        return new CommandResult { RenderedStack = rendered };
    }

    /// <summary>Whether the bundle has its own .env, which compose then reads for variables.</summary>
    public static bool HasEnvFile(StackLayout layout) =>
        layout.Files.Any(f => string.Equals(f.Path, ComposeInvocation.DotEnv, StringComparison.Ordinal));

    // On tmpfs next to the secrets, so decrypted files never reach a disk; owner only.
    private string RenderRoot()
    {
        var root = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetTempPath(), "container-forest-render")
            : Path.Combine(options.Value.SecretsDirectory, ".render");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(root);
        }
        else
        {
            Directory.CreateDirectory(root, OwnerOnlyDirectory);
        }

        return root;
    }

    private static async Task WriteAsync(StackLayout layout, string directory, CancellationToken cancellationToken)
    {
        CreateDirectory(directory);
        foreach (var file in layout.Files)
        {
            var path = StackLayout.ResolveIn(directory, file.Path);
            CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, file.Content, cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, file.Mode);
            }
        }
    }

    private static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, OwnerOnlyDirectory);
        }
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not delete the render directory {Directory}", directory);
        }
    }
}
