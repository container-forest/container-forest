using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Policies;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Deployments;

/// <summary>
/// State of one deployment as it moves through the pipeline. Each step reads what earlier steps produced
/// and adds its own result; reading a result before the step that produces it has run is a bug.
/// </summary>
/// <param name="connection">The repository's connection; null for a public repository.</param>
/// <param name="policy">The environment's compose policy; off when null.</param>
public sealed class DeployContext(
    Deployment deployment,
    Stack stack,
    DockerEnvironment environment,
    GitRepository repository,
    TimeProvider time,
    GitConnection? connection = null,
    ComposePolicy? policy = null)
{
    private GitSnapshot? source;
    private ComposeFile? compose;
    private StackBundle? bundle;

    public Deployment Deployment { get; } = deployment;

    public Stack Stack { get; } = stack;

    public DockerEnvironment Environment { get; } = environment;

    public GitRepository Repository { get; } = repository;

    /// <summary>The repository's connection, with the token to fetch with; null for a public repository.</summary>
    public GitConnection? Connection { get; } = connection;

    /// <summary>The environment's compose policy (ADR-0012), as it was when the deployment started.</summary>
    public ComposePolicy Policy { get; } = policy ?? ComposePolicy.Default(environment.Id);

    /// <summary>
    /// What the compose policy found in the rendered stack; set by <see cref="Steps.ComposePolicyStep"/> when the policy
    /// is on, and kept on the stack once the deployment succeeds.
    /// </summary>
    public ComposeFacts? ComposeFacts { get; set; }

    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>Files of the stack directory at the deployed commit; set by <see cref="Steps.GitFetchStep"/>.</summary>
    public GitSnapshot Source
    {
        get => source ?? throw NotYetProduced(nameof(Source));
        set => source = value;
    }

    /// <summary>The parsed compose file of the stack; set by <see cref="Steps.ValidateComposeStep"/>.</summary>
    public ComposeFile Compose
    {
        get => compose ?? throw NotYetProduced(nameof(Compose));
        set => compose = value;
    }

    /// <summary>What is sent to the agent; set by <see cref="Steps.BuildBundleStep"/>.</summary>
    public StackBundle Bundle
    {
        get => bundle ?? throw NotYetProduced(nameof(Bundle));
        set => bundle = value;
    }

    /// <summary>True when the bundle carries SOPS-decrypted files; false before a bundle is built.</summary>
    public bool HasSecretFiles => bundle?.Files.Any(f => f.IsSecret) ?? false;

    public void Info(string text) => Deployment.AppendLog(DeploymentLogLevel.Info, text, Now);

    public void Warn(string text) => Deployment.AppendLog(DeploymentLogLevel.Warning, text, Now);

    public void Error(string text) => Deployment.AppendLog(DeploymentLogLevel.Error, text, Now);

    private static InvalidOperationException NotYetProduced(string name) =>
        new($"{name} is read before the pipeline step that produces it has run; check the order of the steps.");
}
