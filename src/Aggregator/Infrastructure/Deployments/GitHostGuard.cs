using System.Net;
using System.Net.Sockets;
using ContainerForest.Aggregator.Application.Deployments;

namespace ContainerForest.Aggregator.Infrastructure.Deployments;

/// <summary>
/// Refuses to fetch a public repository from an address inside the aggregator's own networks (ADR-0006): anyone with
/// the Editor role can add one, and the fetch, made from the aggregator, could otherwise probe internal services or the
/// cloud metadata endpoint. The host is resolved here and again by libgit2, so DNS rebinding is not ruled out; probing
/// is. Hosts of connections are trusted: an administrator set them up.
/// </summary>
internal sealed class GitHostGuard(Func<string, CancellationToken, Task<IPAddress[]>> resolve)
{
    public GitHostGuard()
        : this(Dns.GetHostAddressesAsync)
    {
    }

    /// <exception cref="DeploymentFailedException">The host does not resolve, or resolves to a non-public address.</exception>
    public async Task EnsurePublicAsync(Uri url, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(url.IdnHost.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolve(url.IdnHost, cancellationToken);
            }
            catch (SocketException)
            {
                throw new DeploymentFailedException($"Host {url.Host} of {url} does not resolve.");
            }
        }

        if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
        {
            throw new DeploymentFailedException(
                $"Host {url.Host} of {url} is on a private network. A repository there needs a git connection " +
                "set up by an administrator, or the aggregator setting Git:AllowPrivateHosts.");
        }
    }

    /// <summary>False for loopback, private, shared (CGNAT), link-local, multicast, unspecified and reserved addresses.</summary>
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 or >= 224
                || (b[0] == 100 && b[1] is >= 64 and < 128)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and < 32)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && b[1] is 18 or 19));
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.Equals(IPAddress.IPv6None)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC);
        }

        return false;
    }
}
