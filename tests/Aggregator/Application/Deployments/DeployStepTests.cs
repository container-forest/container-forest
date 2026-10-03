using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Deployments.Steps;
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
    public async Task Public_repository_is_fetched_without_credentials()
    {
        var git = new FakeGitSource();
        var context = Context();
        context.Deployment.Start(Now);

        await new GitFetchStep(git, new FakeSecretProtector(), new DeploymentOptions())
            .ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Null(git.FetchedWith);
    }

    [Theory]
    [InlineData("deploy", "deploy")]
    [InlineData(null, GitRepository.DefaultAccessUserName)]
    public async Task Private_repository_is_fetched_with_the_decrypted_token(string? userName, string expectedUserName)
    {
        var git = new FakeGitSource();
        var protector = new FakeSecretProtector();
        var context = Context();
        context.Repository.SetAccessToken(userName, protector.Protect(SecretPurpose.GitAccessToken, "ghp_secret"));
        context.Deployment.Start(Now);

        await new GitFetchStep(git, protector, new DeploymentOptions()).ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(new GitCredentials(expectedUserName, "ghp_secret"), git.FetchedWith);
        Assert.DoesNotContain(context.Deployment.Log, l => l.Text.Contains("ghp_secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_token_fails_the_deployment_with_a_hint()
    {
        var git = new FakeGitSource();
        var context = Context();
        context.Repository.SetAccessToken(null, "protected with a lost key");
        context.Deployment.Start(Now);

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            new GitFetchStep(git, new FakeSecretProtector(), new DeploymentOptions())
                .ExecuteAsync(context, TestContext.Current.CancellationToken));

        Assert.Contains("set it again", error.Message, StringComparison.Ordinal);
        Assert.Null(git.FetchedWith);
    }

    [Fact]
    public void Credentials_do_not_print_the_token() =>
        Assert.DoesNotContain("ghp_secret", new GitCredentials("deploy", "ghp_secret").ToString(), StringComparison.Ordinal);
}

public class ValidateComposeStepTests
{
    [Fact]
    public async Task Pinned_images_pass_without_warnings()
    {
        var context = Fetched(File("compose.yaml"));

        await Run(context, new ComposeService("web", "nginx:1.27", HasBuild: false, Extends: false));

        Assert.Empty(Lines(context, DeploymentLogLevel.Warning));
    }

    [Theory]
    [InlineData("nginx")]
    [InlineData("nginx:latest")]
    public async Task Floating_latest_is_a_warning_not_an_error(string image)
    {
        var context = Fetched(File("compose.yaml"));

        await Run(context, new ComposeService("web", image, HasBuild: false, Extends: false));

        Assert.Contains(Lines(context, DeploymentLogLevel.Warning), l => l.Contains("'web'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Build_is_rejected()
    {
        var context = Fetched(File("compose.yaml"));

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            Run(context, new ComposeService("api", Image: null, HasBuild: true, Extends: false)));
        Assert.Contains("'api'", error.Message);
    }

    [Fact]
    public async Task Service_without_image_is_rejected_unless_it_extends_another()
    {
        await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            Run(Fetched(File("compose.yaml")), new ComposeService("api", Image: null, HasBuild: false, Extends: false)));

        await Run(Fetched(File("compose.yaml")), new ComposeService("api", Image: null, HasBuild: false, Extends: true));
    }

    [Fact]
    public async Task Images_with_variables_are_not_checked()
    {
        var context = Fetched(File("compose.yaml"));

        await Run(context, new ComposeService("web", "${REGISTRY}/web:${TAG:-latest}", HasBuild: false, Extends: false));

        Assert.Empty(Lines(context, DeploymentLogLevel.Warning));
    }

    [Fact]
    public async Task Missing_compose_file_is_an_error()
    {
        var context = Fetched("docker-compose.yml", File("compose.yaml"));

        await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            Run(context, new ComposeService("web", "nginx:1.27", HasBuild: false, Extends: false)));
    }

    [Fact]
    public async Task Compose_without_services_is_an_error()
    {
        await Assert.ThrowsAsync<DeploymentFailedException>(() => Run(Fetched(File("compose.yaml"))));
    }

    private static Task Run(DeployContext context, params ComposeService[] services) =>
        new ValidateComposeStep(new FakeComposeParser(services)).ExecuteAsync(context, TestContext.Current.CancellationToken);
}

public class BuildBundleStepTests
{
    [Fact]
    public async Task Compose_file_is_renamed_and_modes_are_kept()
    {
        var context = Fetched(
            "docker-compose.prod.yml",
            File("docker-compose.prod.yml", "services: {}"),
            File("scripts/init.sh", "#!/bin/sh", executable: true));

        await Run(context);

        var bundle = context.Bundle;
        Assert.Equal(["compose.yaml", "scripts/init.sh"], bundle.Files.Select(f => f.Path.Value));
        Assert.Equal([BundleFile.RegularMode, BundleFile.ExecutableMode], bundle.Files.Select(f => f.Mode));
        Assert.Equal(Commit, bundle.Commit);
        Assert.Equal("web", bundle.StackName.Value);
    }

    [Fact]
    public async Task Another_compose_yaml_next_to_the_compose_file_is_a_conflict()
    {
        var context = Fetched("docker-compose.yml", File("docker-compose.yml"), File("compose.yaml"));

        await Assert.ThrowsAsync<DeploymentFailedException>(() => Run(context));
    }

    [Fact]
    public async Task Bundle_over_the_limit_is_rejected()
    {
        var context = Fetched(File("compose.yaml", new string('x', 2048)));

        await Assert.ThrowsAsync<DeploymentFailedException>(() => Run(context, maxBytes: 1024));
    }

    private static Task Run(DeployContext context, long maxBytes = 1024 * 1024) =>
        new BuildBundleStep(new DeploymentOptions { MaxBundleBytes = maxBytes })
            .ExecuteAsync(context, TestContext.Current.CancellationToken);
}

public class SendToAgentStepTests
{
    [Fact]
    public async Task Compose_output_goes_to_the_log()
    {
        var agent = new FakeAgentChannel { DeployOutput = { "Container web-1 Started" } };
        var context = Bundled();

        await new SendToAgentStep(agent, new DeploymentOptions()).ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Same(context.Bundle, agent.LastBundle);
        Assert.Contains("Container web-1 Started", Lines(context, DeploymentLogLevel.Info));
    }

    [Fact]
    public async Task Output_is_kept_when_the_agent_reports_an_error()
    {
        var context = Bundled();
        var agent = new FakeAgentChannel
        {
            DeployOutput = { "Error response from daemon: pull access denied" },
            DeployError = new AgentCommandFailedException(context.Environment.Id, AgentErrorKind.CommandFailed, "compose up failed"),
        };

        await Assert.ThrowsAsync<AgentCommandFailedException>(() =>
            new SendToAgentStep(agent, new DeploymentOptions()).ExecuteAsync(context, TestContext.Current.CancellationToken));

        Assert.Contains("Error response from daemon: pull access denied", Lines(context, DeploymentLogLevel.Info));
    }

    private static DeployContext Bundled()
    {
        var context = Fetched(File("compose.yaml"));
        context.Bundle = new StackBundle(context.Stack.Name, Commit, [], ImagePullPolicy.Missing);
        return context;
    }
}
