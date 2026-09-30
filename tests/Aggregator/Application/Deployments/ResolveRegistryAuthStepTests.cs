using ContainerForest.Aggregator.Application.Agents;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Application.Deployments.Steps;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;
using ContainerForest.Aggregator.Domain.Stacks;
using static ContainerForest.Aggregator.Application.Tests.Deployments.DeployFixtures;

namespace ContainerForest.Aggregator.Application.Tests.Deployments;

public class ResolveRegistryAuthStepTests
{
    private readonly InMemoryRegistryCredentials credentials = new();
    private readonly FakeSecretProtector protector = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_logins_for_the_registries_of_the_images_are_sent()
    {
        var context = Built("ghcr.io/org/api:1.2", "nginx:1.27");
        Add(context, "ghcr.io", "ghcr-bot", "ghcr-token");
        Add(context, "docker.io", "hub-bot", "hub-token");
        Add(context, "registry.example.com", "other", "other-token");

        await Step().ExecuteAsync(context, Ct);

        Assert.Equal(
            [new RegistryLogin("https://index.docker.io/v1/", "hub-bot", "hub-token"), new RegistryLogin("ghcr.io", "ghcr-bot", "ghcr-token")],
            context.Bundle.RegistryLogins);
        Assert.Contains("Registry credentials: docker.io (hub-bot), ghcr.io (ghcr-bot).", Lines(context, DeploymentLogLevel.Info));
    }

    [Fact]
    public async Task Other_environments_credentials_are_never_sent()
    {
        var context = Built("ghcr.io/org/api:1.2");
        credentials.Add(RegistryCredential.Create(
            Domain.Common.EnvironmentId.New(), "ghcr.io", "foreign", protector.Protect(SecretPurpose.RegistryPassword, "x"), Now));

        await Step().ExecuteAsync(context, Ct);

        Assert.Empty(context.Bundle.RegistryLogins);
    }

    [Theory]
    [InlineData("${REGISTRY}/org/api:1.2")]
    [InlineData(null)]
    public async Task Images_whose_registry_is_unknown_get_all_of_the_environments_logins(string? unknownImage)
    {
        var context = Built("nginx:1.27", unknownImage);
        Add(context, "ghcr.io", "ghcr-bot", "ghcr-token");
        Add(context, "registry.example.com", "other", "other-token");

        await Step().ExecuteAsync(context, Ct);

        Assert.Equal(["ghcr.io", "registry.example.com"], context.Bundle.RegistryLogins.Select(l => l.Server));
    }

    [Fact]
    public async Task Without_matching_credentials_the_bundle_is_unchanged()
    {
        var context = Built("nginx:1.27");
        var bundle = context.Bundle;
        Add(context, "ghcr.io", "ghcr-bot", "ghcr-token");

        await Step().ExecuteAsync(context, Ct);

        Assert.Same(bundle, context.Bundle);
        Assert.DoesNotContain(Lines(context, DeploymentLogLevel.Info), l => l.StartsWith("Registry", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Password_that_cannot_be_decrypted_fails_the_deployment_with_advice()
    {
        var context = Built("ghcr.io/org/api:1.2");
        credentials.Add(RegistryCredential.Create(context.Environment.Id, "ghcr.io", "bot", "garbage", Now));

        var ex = await Assert.ThrowsAsync<DeploymentFailedException>(() => Step().ExecuteAsync(context, Ct));

        Assert.Contains("ghcr.io", ex.Message);
        Assert.Contains("set the credentials", ex.Message);
    }

    [Fact]
    public void Logins_never_print_their_password()
    {
        Assert.DoesNotContain("hunter2", new RegistryLogin("ghcr.io", "bot", "hunter2").ToString());
    }

    private ResolveRegistryAuthStep Step() => new(credentials, protector);

    private void Add(DeployContext context, string server, string username, string password) =>
        credentials.Add(RegistryCredential.Create(
            context.Environment.Id, server, username, protector.Protect(SecretPurpose.RegistryPassword, password), Now));

    /// <summary>A context as it is after <see cref="BuildBundleStep"/>, with one service per image.</summary>
    private static DeployContext Built(params string?[] images)
    {
        var context = Fetched(File("compose.yaml"));
        context.Compose = new ComposeFile(images.Select((image, i) => new ComposeService($"s{i}", image, HasBuild: false, Extends: image is null)).ToList());
        context.Bundle = new StackBundle(StackName.Parse("web"), Commit, [], ImagePullPolicy.Missing);
        return context;
    }
}
