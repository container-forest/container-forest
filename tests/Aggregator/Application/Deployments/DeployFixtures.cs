using System.Text;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Tests.Deployments;

internal static class DeployFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    public static readonly CommitSha Commit = CommitSha.Parse("0123456789abcdef0123456789abcdef01234567");

    public static DeployContext Context(
        string composePath = "compose.yaml", ImagePullPolicy pull = ImagePullPolicy.Missing, bool isSystem = false)
    {
        var environment = DockerEnvironment.Create("prod", isPrivate: false, Now);
        var repository = GitRepository.Create("infra", "https://git.example.com/infra.git", protectedAccessToken: null, Now);
        var stack = Stack.Create(
            environment.Id,
            StackName.Parse("web"),
            repository.Id,
            BranchName.Parse("main"),
            StackFilePath.Parse(composePath),
            isSystem,
            Now);
        var deployment = Deployment.QueueManual(stack.Id, UserId.New(), pull, Now);
        return new DeployContext(deployment, stack, environment, repository, new FixedTime(Now));
    }

    /// <summary>A context as it is after <see cref="Steps.GitFetchStep"/>.</summary>
    public static DeployContext Fetched(params SourceFile[] files) => Fetched("compose.yaml", files);

    public static DeployContext Fetched(string composePath, params SourceFile[] files)
    {
        var context = Context(composePath);
        context.Deployment.Start(Now);
        context.Deployment.ResolveCommit(Commit);
        context.Source = new GitSnapshot(Commit, files, []);
        return context;
    }

    public static SourceFile File(string path, string content = "", bool executable = false) =>
        new(StackFilePath.Parse(path), Encoding.UTF8.GetBytes(content), executable);

    public static IEnumerable<string> Lines(DeployContext context, DeploymentLogLevel level) =>
        context.Deployment.Log.Where(l => l.Level == level).Select(l => l.Text);
}

internal sealed class FakeGitSource : IGitSource
{
    public GitSnapshot Snapshot { get; set; } = new(DeployFixtures.Commit, [DeployFixtures.File("compose.yaml")], []);

    public string? RequestedDirectory { get; private set; }

    public Task<GitSnapshot> FetchAsync(
        GitRepository repository, BranchName branch, string directory, long maxBytes, CancellationToken cancellationToken)
    {
        RequestedDirectory = directory;
        return Task.FromResult(Snapshot);
    }
}

internal sealed class FakeComposeParser(params ComposeService[] services) : IComposeParser
{
    public ComposeFile Parse(string yaml) => new(services);
}

/// <summary>A step that records that it ran and optionally fails.</summary>
internal sealed class RecordingStep(string name, List<string> journal, Exception? failure = null) : IDeployStep
{
    public string Name => name;

    public Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
    {
        journal.Add(name);
        return failure is null ? Task.CompletedTask : Task.FromException(failure);
    }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
