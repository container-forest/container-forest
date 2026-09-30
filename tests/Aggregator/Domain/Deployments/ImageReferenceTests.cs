using ContainerForest.Aggregator.Domain.Deployments;

namespace ContainerForest.Aggregator.Domain.Tests.Deployments;

public class ImageReferenceTests
{
    [Theory]
    [InlineData("nginx", "nginx", null, null)]
    [InlineData("nginx:1.27", "nginx", "1.27", null)]
    [InlineData("ghcr.io/org/app:2.0.1", "ghcr.io/org/app", "2.0.1", null)]
    [InlineData("localhost:5000/app", "localhost:5000/app", null, null)]
    [InlineData("localhost:5000/app:dev", "localhost:5000/app", "dev", null)]
    [InlineData("redis@sha256:abc", "redis", null, "sha256:abc")]
    [InlineData("redis:7@sha256:abc", "redis", "7", "sha256:abc")]
    public void Splits_name_tag_and_digest(string value, string name, string? tag, string? digest)
    {
        Assert.True(ImageReference.TryParse(value, out var image));
        Assert.Equal(name, image.Name);
        Assert.Equal(tag, image.Tag);
        Assert.Equal(digest, image.Digest);
        Assert.Equal(value, image.ToString());
    }

    [Theory]
    [InlineData("nginx", true)]
    [InlineData("nginx:latest", true)]
    [InlineData("localhost:5000/app", true)]
    [InlineData("nginx:1.27", false)]
    [InlineData("nginx@sha256:abc", false)]
    [InlineData("nginx:latest@sha256:abc", false)]
    public void Floating_latest_is_an_implicit_or_explicit_latest_without_a_digest(string value, bool expected)
    {
        Assert.True(ImageReference.TryParse(value, out var image));
        Assert.Equal(expected, image.IsFloatingLatest);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("nginx:")]
    [InlineData("nginx@")]
    [InlineData(":1.0")]
    [InlineData("my image:1")]
    public void Rejects_malformed_references(string value) => Assert.False(ImageReference.TryParse(value, out _));
}
