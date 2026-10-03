using System.Net;
using System.Net.Sockets;
using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Infrastructure.Deployments;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Deployments;

public class GitHostGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void Private_and_special_addresses_are_not_public(string address) =>
        Assert.False(GitHostGuard.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("140.82.112.3")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:50c0:8000::153")]
    public void Internet_addresses_are_public(string address) =>
        Assert.True(GitHostGuard.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task A_host_with_any_private_address_is_refused()
    {
        var guard = new GitHostGuard((_, _) => Task.FromResult(new[] { IPAddress.Parse("140.82.112.3"), IPAddress.Parse("10.0.0.1") }));

        await Assert.ThrowsAsync<DeploymentFailedException>(() => guard.EnsurePublicAsync(new Uri("https://git.example.com/a.git"), Ct));
    }

    [Fact]
    public async Task An_address_literal_is_checked_without_resolving()
    {
        var guard = new GitHostGuard((_, _) => throw new InvalidOperationException("must not resolve"));

        await Assert.ThrowsAsync<DeploymentFailedException>(() => guard.EnsurePublicAsync(new Uri("https://[::1]/a.git"), Ct));
        await guard.EnsurePublicAsync(new Uri("https://140.82.112.3/a.git"), Ct);
    }

    [Fact]
    public async Task A_host_that_does_not_resolve_fails_the_deployment()
    {
        var guard = new GitHostGuard((_, _) => throw new SocketException((int)SocketError.HostNotFound));

        await Assert.ThrowsAsync<DeploymentFailedException>(() => guard.EnsurePublicAsync(new Uri("https://nowhere.example/a.git"), Ct));
    }

    [Fact]
    public async Task A_public_host_passes()
    {
        var guard = new GitHostGuard((_, _) => Task.FromResult(new[] { IPAddress.Parse("140.82.112.3") }));

        await guard.EnsurePublicAsync(new Uri("https://github.com/a/b.git"), Ct);
    }
}
