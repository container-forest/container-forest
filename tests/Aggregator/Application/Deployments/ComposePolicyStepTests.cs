using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Deployments.Steps;
using ContainerForest.Aggregator.Application.Policies;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Policies;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Deployments;

/// <summary>The compose policy step of the pipeline (ADR-0012).</summary>
public class ComposePolicyStepTests
{
    private const string StackDirectory = "/opt/container-forest/stacks/web";

    private const string PrivilegedModel = """
        {"services":{"web":{"image":"nginx","privileged":true,
          "volumes":[{"type":"bind","source":"/opt/container-forest/stacks/web/conf","target":"/conf","read_only":true},
                     {"type":"bind","source":"/etc/localtime","target":"/etc/localtime","read_only":true}]}}}
        """;

    private const string PlainModel = """{"services":{"web":{"image":"nginx"}}}""";

    private readonly FakeAgentChannel agent = new();
    private readonly InMemoryUsers users = new();
    private readonly InMemoryRoleAssignments roleAssignments = new();
    private readonly InMemoryImageUpdateRecords updates = new();
    private readonly InMemoryImagePolicies imagePolicies = new();
    private readonly InMemoryStacks stacks = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_a_policy_the_agent_is_not_asked()
    {
        var context = Bundled(ComposePolicyMode.Off);

        await Step().ExecuteAsync(context, Ct);

        Assert.Empty(agent.Calls);
        Assert.False(context.Bundle.ExplicitEnvFile);
    }

    [Fact]
    public async Task Enforce_fails_a_violating_stack_and_names_the_allowance()
    {
        agent.Rendered = Rendered(PrivilegedModel);
        var context = Bundled(ComposePolicyMode.Enforce);

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));

        Assert.Contains("allowance 'privileged'", error.Message, StringComparison.Ordinal);
        Assert.Equal(["render web"], agent.Calls);
    }

    [Fact]
    public async Task Enforce_passes_a_compliant_stack_and_has_the_agent_check_its_mount_sources()
    {
        agent.Rendered = Rendered(PrivilegedModel);
        var context = Bundled(ComposePolicyMode.Enforce);
        context.Stack.SetPolicyAllowances(["privileged"]);

        await Step().ExecuteAsync(context, Ct);

        Assert.True(context.Bundle.ExplicitEnvFile);
        Assert.Equal([$"{StackDirectory}/conf"], context.Bundle.VerifyPaths);
        Assert.NotNull(context.ComposeFacts);
    }

    [Fact]
    public async Task Audit_only_warns()
    {
        agent.Rendered = Rendered(PrivilegedModel);
        var context = Bundled(ComposePolicyMode.Audit);

        await Step().ExecuteAsync(context, Ct);

        Assert.Contains(Lines(context, DeploymentLogLevel.Warning), l => l.Contains("privileged", StringComparison.Ordinal));
        Assert.Empty(context.Bundle.VerifyPaths);
        Assert.True(context.Bundle.ExplicitEnvFile);
    }

    [Fact]
    public async Task Content_signed_by_the_environment_administrator_is_exempt()
    {
        agent.Rendered = Rendered(PrivilegedModel);
        var owner = User.Create("owner", Now);
        users.Add(owner);
        var context = Bundled(ComposePolicyMode.Enforce);
        roleAssignments.Add(RoleAssignment.ForEnvironment(owner.Id, context.Environment.Id, Role.EnvironmentAdmin, Now));
        context.Deployment.RecordSigned(owner.Id, owner.UserName, "SHA256:x");

        await Step().ExecuteAsync(context, Ct);

        Assert.Contains(Lines(context, DeploymentLogLevel.Info), l => l.Contains("endorsed by owner", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pressing_deploy_as_the_environment_administrator_is_not_an_endorsement()
    {
        agent.Rendered = Rendered(PrivilegedModel);
        var owner = User.Create("owner", Now);
        users.Add(owner);
        var context = Bundled(ComposePolicyMode.Enforce, requestedBy: owner);
        roleAssignments.Add(RoleAssignment.ForEnvironment(owner.Id, context.Environment.Id, Role.EnvironmentAdmin, Now));

        await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));
    }

    [Fact]
    public async Task Signature_of_an_editor_is_no_exemption()
    {
        agent.Rendered = Rendered(PrivilegedModel);
        var editor = User.Create("editor", Now);
        users.Add(editor);
        var context = Bundled(ComposePolicyMode.Enforce);
        roleAssignments.Add(RoleAssignment.ForEnvironment(editor.Id, context.Environment.Id, Role.Editor, Now));
        context.Deployment.RecordSigned(editor.Id, editor.UserName, "SHA256:x");

        await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));
    }

    [Theory]
    [InlineData(ComposePolicyMode.Audit, false)]
    [InlineData(ComposePolicyMode.Enforce, true)]
    public async Task Agent_too_old_to_render_fails_only_under_enforce(ComposePolicyMode mode, bool fails)
    {
        var context = Bundled(mode);

        var run = Step().ExecuteAsync(context, Ct);

        if (fails)
        {
            await Assert.ThrowsAsync<DeploymentFailedException>(() => run);
        }
        else
        {
            await run;
            Assert.Contains(Lines(context, DeploymentLogLevel.Warning), l => l.Contains("too old", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Rehydrating_the_deployed_commit_is_not_checked_again()
    {
        var context = Bundled(ComposePolicyMode.Enforce);
        var earlier = Deployment.QueueManual(context.Stack.Id, UserId.New(), ImagePullPolicy.Missing, Now);
        earlier.Start(Now);
        earlier.ResolveCommit(Commit);
        earlier.Succeed(Now);
        context.Stack.RecordDeployment(earlier, withSecrets: true);
        var rehydrate = Deployment.QueueRehydrate(context.Stack.Id, Commit, Now);
        rehydrate.Start(Now);
        rehydrate.ResolveCommit(Commit);
        var again = new DeployContext(rehydrate, context.Stack, context.Environment, context.Repository, new FixedTime(Now), null, context.Policy)
        {
            Bundle = context.Bundle,
        };

        await Step().ExecuteAsync(again, Ct);

        Assert.Empty(agent.Calls);
        Assert.True(again.Bundle.ExplicitEnvFile);
    }

    [Fact]
    public async Task Compliant_stack_passes_without_warnings()
    {
        agent.Rendered = Rendered(PlainModel);
        var context = Bundled(ComposePolicyMode.Enforce);

        await Step().ExecuteAsync(context, Ct);

        Assert.Empty(Lines(context, DeploymentLogLevel.Warning));
    }

    [Fact]
    public async Task Unrestricted_stack_is_rendered_and_reported_but_not_limited()
    {
        agent.Rendered = Rendered(PrivilegedModel);
        var context = Bundled(ComposePolicyMode.Enforce);
        context.Stack.SetPolicyUnrestricted(true);

        await Step().ExecuteAsync(context, Ct);

        Assert.Empty(context.Bundle.VerifyPaths);
        Assert.NotNull(context.ComposeFacts);
        Assert.Contains(Lines(context, DeploymentLogLevel.Info), l => l.Contains("not applied", StringComparison.Ordinal));
        Assert.Empty(Lines(context, DeploymentLogLevel.Warning));
    }

    [Fact]
    public async Task Unrestricted_stack_deploys_through_an_agent_too_old_to_render()
    {
        var context = Bundled(ComposePolicyMode.Enforce);
        context.Stack.SetPolicyUnrestricted(true);

        await Step().ExecuteAsync(context, Ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_commit_keeps_the_exemption_only_when_an_administrator_set_its_policy(bool byAdministrator)
    {
        agent.Rendered = Rendered(PrivilegedModel);
        var owner = User.Create("owner", Now);
        users.Add(owner);
        var context = Bundled(ComposePolicyMode.Enforce);
        roleAssignments.Add(RoleAssignment.ForEnvironment(owner.Id, context.Environment.Id, Role.EnvironmentAdmin, Now));
        var policy = Domain.Images.ImagePolicy.Create(
            context.Stack.Id, Domain.Images.ImageName.Parse("app"), new Domain.Images.ImagePolicySettings(@"^v[0-9]+\z", Domain.Images.ImageTagOrder.ImageCreated, []),
            owner.Id, byAdministrator, Now);
        imagePolicies.Add(policy);
        var record = Domain.Images.ImageUpdateRecord.Create(
            ImageUpdateRecordId.New(), context.Stack.Id, policy.Id, Commit, CommitSha.Parse(new string('2', 40)), context.Stack.ComposePath, "v2", null,
            [new Domain.Images.ImageChange("app", "app:v1", "app:v2")], Domain.Images.ImageUpdateReason.Policy, null, null, Now);
        updates.Add(record);
        context.Deployment.RecordImageUpdate(record.Id, (owner.Id, owner.UserName, "SHA256:x"));

        var run = Step().ExecuteAsync(context, Ct);

        if (byAdministrator)
        {
            await run;
            Assert.Contains(Lines(context, DeploymentLogLevel.Info), l => l.Contains("endorsed by owner", StringComparison.Ordinal));
        }
        else
        {
            await Assert.ThrowsAsync<DeploymentFailedException>(() => run);
        }
    }

    private ComposePolicyStep Step() => new(agent, users, roleAssignments, stacks, updates, imagePolicies, new DeploymentOptions());

    private static RenderedCompose Rendered(string model) =>
        new(model, StackDirectory, ["/opt/container-forest", "/run/container-forest"], []);

    private static DeployContext Bundled(ComposePolicyMode mode, User? requestedBy = null)
    {
        var context = Context(policyMode: mode, requestedBy: requestedBy?.Id);
        context.Deployment.Start(Now);
        context.Deployment.ResolveCommit(Commit);
        context.Source = new GitSnapshot(Commit, [File("compose.yaml", "services: {}")], []);
        context.Bundle = new StackBundle(context.Stack.Name, Commit, [], ImagePullPolicy.Missing);
        return context;
    }
}
