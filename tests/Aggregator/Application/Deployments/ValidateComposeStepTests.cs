using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Deployments.Steps;
using ContainerForest.Aggregator.Domain.Deployments;
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

    private static ValidateComposeStep Step(params ComposeService[] services) => new(new FakeComposeParser(services));
}
