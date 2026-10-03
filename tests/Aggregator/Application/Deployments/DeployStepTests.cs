using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Deployments.Steps;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Repositories;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Deployments;

public class GitFetchStepTests
{
    [Fact]
    public async Task Reads_the_compose_file_directory_and_pins_the_commit()
    {
        var git = new FakeGitSource
        {
            Snapshot = new GitSnapshot(Commit, [File("compose.yaml")], ["current-link"]),
        };
        var context = Context("apps/web/compose.yaml");
        context.Deployment.Start(Now);

        await new GitFetchStep(git, new FakeSecretProtector(), new DeploymentOptions()).ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal("apps/web", git.RequestedDirectory);
        Assert.Equal(Commit, context.Deployment.Commit);
        Assert.Single(context.Source.Files);
        Assert.Contains(Lines(context, DeploymentLogLevel.Warning), l => l.Contains("current-link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Commit_stays_pinned_when_reading_its_files_fails()
    {
        var git = new FakeGitSource { ReadError = new DeploymentFailedException("Directory 'apps/web' does not exist.") };
        var context = Context("apps/web/compose.yaml");
        context.Deployment.Start(Now);

        await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            new GitFetchStep(git, new FakeSecretProtector(), new DeploymentOptions()).ExecuteAsync(context, TestContext.Current.CancellationToken));

        Assert.Equal(Commit, context.Deployment.Commit);
    }

    [Fact]
    public async Task Pinned_deployment_reads_its_own_commit_not_the_branch_head()
    {
        var pinned = CommitSha.Parse(new string('b', 40));
        var git = new FakeGitSource();
        var context = PinnedContext(pinned);

        await new GitFetchStep(git, new FakeSecretProtector(), new DeploymentOptions()).ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(pinned, git.ReadCommit);
        Assert.Equal(pinned, context.Deployment.Commit);
        Assert.NotNull(git.FetchedBranch);
    }

    [Fact]
    public async Task Pinned_deployment_uses_the_cache_when_the_git_server_is_down()
    {
        var pinned = CommitSha.Parse(new string('b', 40));
        var git = new FakeGitSource { FetchError = new DeploymentFailedException("Fetching https://git.example.com/infra.git failed.") };
        var context = PinnedContext(pinned);

        await new GitFetchStep(git, new FakeSecretProtector(), new DeploymentOptions()).ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(pinned, git.ReadCommit);
        Assert.Contains(Lines(context, DeploymentLogLevel.Warning), l => l.Contains("from the aggregator's cache", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unpinned_deployment_fails_when_the_git_server_is_down()
    {
        var git = new FakeGitSource { FetchError = new DeploymentFailedException("Fetching failed.") };
        var context = Context();
        context.Deployment.Start(Now);

        await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            new GitFetchStep(git, new FakeSecretProtector(), new DeploymentOptions()).ExecuteAsync(context, TestContext.Current.CancellationToken));
    }

    private static DeployContext PinnedContext(CommitSha pinned)
    {
        var fresh = Context();
        var deployment = Deployment.QueueRehydrate(fresh.Stack.Id, pinned, Now);
        deployment.Start(Now);
        return new DeployContext(deployment, fresh.Stack, fresh.Environment, fresh.Repository, new FixedTime(Now));
    }

    [Fact]
    public async Task Public_repository_is_fetched_without_credentials_from_an_untrusted_host()
    {
        var git = new FakeGitSource();
        var context = Context();
        context.Deployment.Start(Now);

        await new GitFetchStep(git, new FakeSecretProtector(), new DeploymentOptions())
            .ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(GitAccess.Public, git.FetchedAccess);
    }

    [Theory]
    [InlineData("deploy", "deploy")]
    [InlineData(null, GitConnection.DefaultAccessUserName)]
    public async Task Repository_of_a_connection_is_fetched_with_its_decrypted_token(string? userName, string expectedUserName)
    {
        var git = new FakeGitSource();
        var protector = new FakeSecretProtector();
        var connection = AllowedConnection();
        connection.SetAccessToken(userName, protector.Protect(SecretPurpose.GitAccessToken, "ghp_secret"));
        var context = Context(connection: connection);
        context.Deployment.Start(Now);

        await new GitFetchStep(git, protector, new DeploymentOptions()).ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(new GitCredentials(expectedUserName, "ghp_secret", connection.UrlPrefix), git.FetchedWith);
        Assert.True(git.FetchedAccess!.IsTrustedHost);
        Assert.DoesNotContain(context.Deployment.Log, l => l.Text.Contains("ghp_secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connection_without_a_token_is_trusted_and_anonymous()
    {
        var git = new FakeGitSource();
        var context = Context(connection: AllowedConnection());
        context.Deployment.Start(Now);

        await new GitFetchStep(git, new FakeSecretProtector(), new DeploymentOptions()).ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(new GitAccess(null, IsTrustedHost: true), git.FetchedAccess);
    }

    [Fact]
    public async Task Environment_taken_off_the_connection_does_not_fetch()
    {
        var git = new FakeGitSource();
        var protector = new FakeSecretProtector();
        var connection = GitConnection.Create("team", "https://git.example.com/", Now);
        connection.AllowEnvironments(false, [EnvironmentId.New()]);
        connection.SetAccessToken(null, protector.Protect(SecretPurpose.GitAccessToken, "ghp_secret"));
        var context = Context(connection: connection);
        context.Deployment.Start(Now);

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            new GitFetchStep(git, protector, new DeploymentOptions()).ExecuteAsync(context, TestContext.Current.CancellationToken));

        Assert.Contains("not allowed here", error.Message, StringComparison.Ordinal);
        Assert.Null(git.FetchedAccess);
    }

    [Fact]
    public async Task Unreadable_token_fails_the_deployment_with_a_hint()
    {
        var git = new FakeGitSource();
        var connection = AllowedConnection();
        connection.SetAccessToken(null, "protected with a lost key");
        var context = Context(connection: connection);
        context.Deployment.Start(Now);

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            new GitFetchStep(git, new FakeSecretProtector(), new DeploymentOptions())
                .ExecuteAsync(context, TestContext.Current.CancellationToken));

        Assert.Contains("set it again", error.Message, StringComparison.Ordinal);
        Assert.Null(git.FetchedAccess);
    }

    [Fact]
    public void Credentials_do_not_print_the_token() =>
        Assert.DoesNotContain(
            "ghp_secret",
            new GitCredentials("deploy", "ghp_secret", new Uri("https://git.example.com/")).ToString(),
            StringComparison.Ordinal);

    private static GitConnection AllowedConnection()
    {
        var connection = GitConnection.Create("team", "https://git.example.com/", Now);
        connection.AllowEnvironments(true, []);
        return connection;
    }
}
