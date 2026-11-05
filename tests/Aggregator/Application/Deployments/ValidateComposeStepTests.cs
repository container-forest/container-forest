using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Deployments.Steps;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Policies;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Deployments;

public class ValidateComposeStepTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tags_are_enough_unless_the_stack_requires_digests()
    {
        var context = Fetched(File("compose.yaml"));

        await Step(new ComposeService("web", "ghcr.io/org/web:1.4", false, false)).ExecuteAsync(context, Ct);

        Assert.Single(context.Compose.Services);
    }

    [Theory]
    [InlineData("ghcr.io/org/web:1.4")]
    [InlineData("nginx")]
    [InlineData("ghcr.io/org/web:${TAG}")]
    public async Task Stack_requiring_digests_refuses_images_without_one(string image)
    {
        var context = Fetched(File("compose.yaml"));
        context.Stack.SetRequireImageDigests(true);

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            Step(new ComposeService("web", image, false, false)).ExecuteAsync(context, Ct));

        Assert.Contains("digest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stack_requiring_digests_refuses_an_image_from_extends()
    {
        var context = Fetched(File("compose.yaml"));
        context.Stack.SetRequireImageDigests(true);

        await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            Step(new ComposeService("web", null, false, Extends: true)).ExecuteAsync(context, Ct));
    }

    [Fact]
    public async Task Pinned_images_pass_without_a_warning()
    {
        var context = Fetched(File("compose.yaml"));
        context.Stack.SetRequireImageDigests(true);

        await Step(
            new ComposeService("web", $"ghcr.io/org/web:1.4@{Digest}", false, false),
            new ComposeService("db", $"postgres@{Digest}", false, false)).ExecuteAsync(context, Ct);

        Assert.Empty(Lines(context, DeploymentLogLevel.Warning));
    }

    [Theory]
    [InlineData("app.env", true)]
    [InlineData("./config/app.env", true)]
    [InlineData("../other/.env", false)]
    [InlineData("/run/container-forest/db/.env", false)]
    [InlineData("${ENV_FILE}", false)]
    [InlineData("missing.env", false)]
    public async Task Under_a_policy_compose_may_read_only_files_of_the_bundle(string reference, bool allowed)
    {
        var context = Fetched(ComposePolicyMode.Audit, "compose.yaml", File("compose.yaml"), File("app.env"), File("config/app.env"));
        var step = Step(new ComposeService("web", "nginx:1.27", false, false, [reference]));

        if (allowed)
        {
            await step.ExecuteAsync(context, Ct);
        }
        else
        {
            var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => step.ExecuteAsync(context, Ct));
            Assert.Contains("compose policy", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Without_a_policy_references_and_tags_are_left_to_compose()
    {
        var context = Fetched(File("compose.yaml"));

        await new ValidateComposeStep(new TaggedParser()).ExecuteAsync(context, Ct);
    }

    [Fact]
    public async Task Under_a_policy_yaml_tags_and_includes_from_elsewhere_are_refused()
    {
        var context = Fetched(ComposePolicyMode.Enforce, "compose.yaml", File("compose.yaml"));

        var error = await Assert.ThrowsAsync<DeploymentFailedException>(() => new ValidateComposeStep(new TaggedParser()).ExecuteAsync(context, Ct));

        Assert.Contains("YAML tags", error.Message, StringComparison.Ordinal);
        Assert.Contains("oci://", error.Message, StringComparison.Ordinal);
    }

    private sealed class TaggedParser : IComposeParser
    {
        public ComposeFile Parse(string yaml) =>
            new([new ComposeService("web", "nginx:1.27", false, false)], ["oci://registry.example.com/app:1"], HasTags: true);
    }

    private static ValidateComposeStep Step(params ComposeService[] services) => new(new FakeComposeParser(services));
}
