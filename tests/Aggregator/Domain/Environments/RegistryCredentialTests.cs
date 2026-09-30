using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Environments;

namespace ContainerForest.Aggregator.Domain.Tests.Environments;

public class RegistryCredentialTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("nginx", "docker.io")]
    [InlineData("nginx:1.27", "docker.io")]
    [InlineData("library/redis", "docker.io")]
    [InlineData("myorg/app:2", "docker.io")]
    [InlineData("docker.io/myorg/app", "docker.io")]
    [InlineData("index.docker.io/myorg/app", "docker.io")]
    [InlineData("ghcr.io/org/app:1.4", "ghcr.io")]
    [InlineData("GHCR.io/org/app", "ghcr.io")]
    [InlineData("localhost/app", "localhost")]
    [InlineData("localhost:5000/app", "localhost:5000")]
    [InlineData("registry.example.com:5000/team/app@sha256:abc", "registry.example.com:5000")]
    public void Registry_of_an_image_follows_docker_rules(string reference, string expected)
    {
        Assert.True(ImageReference.TryParse(reference, out var image));

        Assert.Equal(expected, RegistryHost.Of(image));
    }

    [Theory]
    [InlineData("ghcr.io", "ghcr.io")]
    [InlineData(" GHCR.IO ", "ghcr.io")]
    [InlineData("https://ghcr.io", "ghcr.io")]
    [InlineData("https://ghcr.io/", "ghcr.io")]
    [InlineData("registry.example.com:5000", "registry.example.com:5000")]
    [InlineData("docker.io", "docker.io")]
    [InlineData("index.docker.io", "docker.io")]
    [InlineData("https://index.docker.io/v1/", "docker.io")]
    [InlineData("10.0.0.5:5000", "10.0.0.5:5000")]
    public void Server_names_are_normalized(string value, string expected)
    {
        Assert.True(RegistryHost.TryParse(value, out var host));
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ghcr.io/org")]
    [InlineData("ghcr.io/v1/")]
    [InlineData("ghcr io")]
    [InlineData("ghcr.io\nevil.example.com")]
    [InlineData("-ghcr.io")]
    [InlineData("user:pass@ghcr.io")]
    public void Invalid_server_names_are_rejected(string value)
    {
        Assert.False(RegistryHost.TryParse(value, out _));
        Assert.Throws<DomainValidationException>(() => RegistryCredential.ParseServer(value));
    }

    [Fact]
    public void Docker_hub_has_its_legacy_key_in_the_docker_config()
    {
        Assert.Equal("https://index.docker.io/v1/", RegistryHost.ConfigKey("docker.io"));
        Assert.Equal("ghcr.io", RegistryHost.ConfigKey("ghcr.io"));
    }

    [Fact]
    public void Credential_stores_the_normalized_server_and_the_protected_password()
    {
        var environmentId = EnvironmentId.New();

        var credential = RegistryCredential.Create(environmentId, "https://GHCR.io", " bot ", "protected:abc", Now);

        Assert.Equal(environmentId, credential.EnvironmentId);
        Assert.Equal("ghcr.io", credential.Server);
        Assert.Equal("bot", credential.Username);
        Assert.Equal("protected:abc", credential.ProtectedPassword);
        Assert.Equal(Now, credential.CreatedAt);
        Assert.Equal(Now, credential.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("b\not")]
    public void Unusable_user_names_are_rejected(string username)
    {
        Assert.Throws<DomainValidationException>(() => RegistryCredential.Create(EnvironmentId.New(), "ghcr.io", username, "p", Now));
    }

    [Fact]
    public void Empty_password_is_rejected()
    {
        Assert.Throws<DomainValidationException>(() => RegistryCredential.Create(EnvironmentId.New(), "ghcr.io", "bot", "", Now));
    }

    [Fact]
    public void Changing_the_login_keeps_the_server_and_updates_the_time()
    {
        var credential = RegistryCredential.Create(EnvironmentId.New(), "ghcr.io", "bot", "old", Now);

        credential.ChangeLogin("ci", "new", Now.AddDays(1));

        Assert.Equal("ghcr.io", credential.Server);
        Assert.Equal("ci", credential.Username);
        Assert.Equal("new", credential.ProtectedPassword);
        Assert.Equal(Now, credential.CreatedAt);
        Assert.Equal(Now.AddDays(1), credential.UpdatedAt);
    }

    [Fact]
    public void Credential_serves_only_images_of_its_registry()
    {
        var hub = RegistryCredential.Create(EnvironmentId.New(), "docker.io", "bot", "p", Now);
        var ghcr = RegistryCredential.Create(EnvironmentId.New(), "ghcr.io", "bot", "p", Now);
        ImageReference.TryParse("myorg/app:1", out var hubImage);
        ImageReference.TryParse("ghcr.io/myorg/app:1", out var ghcrImage);

        Assert.True(hub.Serves(hubImage!));
        Assert.False(hub.Serves(ghcrImage!));
        Assert.True(ghcr.Serves(ghcrImage!));
    }
}
