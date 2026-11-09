using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ContainerForest.Aggregator.Application.Images;
using ContainerForest.Aggregator.Domain.Deployments;
using ContainerForest.Aggregator.Domain.Images;
using ContainerForest.Aggregator.Infrastructure.Deployments;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Infrastructure.Images;

/// <summary>
/// Reads registries with the OCI distribution API (ADR-0015, item 9): the tag list, manifests and the configuration blob
/// for the "created" time. Authenticates as Docker does: anonymously first, then with a bearer token from the realm the
/// registry names (with the environment's login, when there is one), or with basic authentication.
/// </summary>
/// <remarks>
/// An Editor chooses the image, so the aggregator would connect wherever its name points: connections to private,
/// loopback and link-local addresses are refused when the socket opens (not only when the name is first resolved), unless
/// <see cref="ImageUpdateOptions.AllowPrivateRegistries"/>. Redirects are followed by hand, each one checked the same way,
/// and credentials are never sent to another host than the one they are for.
/// </remarks>
internal sealed partial class OciRegistryClient : IRegistryClient, IDisposable
{
    public const int MaxManifestBytes = 4 * 1024 * 1024;
    public const int MaxConfigBytes = 8 * 1024 * 1024;
    private const int MaxTagPages = 50;
    private const int MaxRedirects = 5;

    private static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    private readonly HttpClient http;
    private readonly ImageUpdateOptions options;
    private readonly ILogger<OciRegistryClient> logger;
    private readonly ConcurrentDictionary<string, (string Header, DateTimeOffset Expires)> tokens = new();
    private readonly TimeProvider time;

    public OciRegistryClient(ImageUpdateOptions options, TimeProvider time, ILogger<OciRegistryClient> logger)
        : this(CreateHandler(options), options, time, logger)
    {
    }

    /// <summary>For tests: any handler, e.g. one that answers without a network.</summary>
    internal OciRegistryClient(HttpMessageHandler handler, ImageUpdateOptions options, TimeProvider time, ILogger<OciRegistryClient> logger)
    {
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("container-forest");
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    public void Dispose() => http.Dispose();

    public async Task<IReadOnlyList<string>> ListTagsAsync(ImageName image, RegistryAuth? auth, CancellationToken cancellationToken)
    {
        var tags = new List<string>();
        Uri? next = new(Base(image), $"{image.Repository}/tags/list?n=1000");
        for (var page = 0; next is not null && page < MaxTagPages; page++)
        {
            using var response = await SendAsync(image, auth, next, [], cancellationToken);
            EnsureSuccess(response, image, null);
            var body = await ReadAsync(response, MaxManifestBytes, cancellationToken);
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("tags", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    tags.AddRange(list.EnumerateArray().Select(t => t.GetString()).OfType<string>());
                }
            }
            catch (JsonException)
            {
                throw new RegistryUnavailableException($"{image.Registry} answered the tag list of {image} with something that is not JSON.");
            }

            next = NextPage(response, next);
        }

        return tags;
    }

    public async Task<RegistryImage> GetImageAsync(
        ImageName image, string tag, RegistryAuth? auth, bool readCreated, CancellationToken cancellationToken)
    {
        var (digest, manifest, mediaType) = await GetManifestAsync(image, tag, auth, cancellationToken);
        if (!readCreated)
        {
            return new RegistryImage(digest, null);
        }

        // For an index, the first platform image's configuration: platforms of one build share its time.
        if (IsIndex(mediaType, manifest))
        {
            var platform = PlatformDigest(manifest)
                ?? throw new RegistryUnavailableException($"The index of {image}:{tag} lists no linux image.");
            (_, manifest, _) = await GetManifestAsync(image, platform, auth, cancellationToken);
        }

        var config = ConfigDigest(manifest)
            ?? throw new RegistryUnavailableException($"The manifest of {image}:{tag} has no configuration.");
        using var response = await SendAsync(image, auth, new Uri(Base(image), $"{image.Repository}/blobs/{config}"), [], cancellationToken);
        EnsureSuccess(response, image, tag);
        var body = await ReadAsync(response, MaxConfigBytes, cancellationToken);
        return new RegistryImage(digest, Created(body));
    }

    private async Task<(string Digest, JsonElement Manifest, string? MediaType)> GetManifestAsync(
        ImageName image, string reference, RegistryAuth? auth, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(image, auth, new Uri(Base(image), $"{image.Repository}/manifests/{reference}"), ManifestTypes, cancellationToken);
        EnsureSuccess(response, image, reference);
        var body = await ReadAsync(response, MaxManifestBytes, cancellationToken);
        var digest = response.Headers.TryGetValues("Docker-Content-Digest", out var values) && values.FirstOrDefault() is { } header
            && DigestPattern().IsMatch(header)
                ? header
                : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body));
        try
        {
            using var json = JsonDocument.Parse(body);
            return (digest, json.RootElement.Clone(), response.Content.Headers.ContentType?.MediaType);
        }
        catch (JsonException)
        {
            throw new RegistryUnavailableException($"{image.Registry} answered the manifest of {image}:{reference} with something that is not JSON.");
        }
    }

    private static bool IsIndex(string? mediaType, JsonElement manifest) =>
        mediaType is "application/vnd.oci.image.index.v1+json" or "application/vnd.docker.distribution.manifest.list.v2+json"
        || (manifest.TryGetProperty("manifests", out var list) && list.ValueKind == JsonValueKind.Array);

    private static string? PlatformDigest(JsonElement index)
    {
        if (!index.TryGetProperty("manifests", out var manifests) || manifests.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Attestations are listed with the platform "unknown/unknown".
        return manifests.EnumerateArray()
            .Where(m => m.TryGetProperty("platform", out var p) && p.TryGetProperty("os", out var os) && os.GetString() == "linux"
                && (!p.TryGetProperty("architecture", out var arch) || arch.GetString() != "unknown"))
            .Select(m => m.TryGetProperty("digest", out var d) ? d.GetString() : null)
            .FirstOrDefault(d => d is not null && DigestPattern().IsMatch(d));
    }

    private static string? ConfigDigest(JsonElement manifest) =>
        manifest.TryGetProperty("config", out var config) && config.TryGetProperty("digest", out var digest)
            && digest.GetString() is { } value && DigestPattern().IsMatch(value)
            ? value
            : null;

    private static DateTimeOffset? Created(byte[] config)
    {
        try
        {
            using var json = JsonDocument.Parse(config);
            return json.RootElement.TryGetProperty("created", out var created) && created.GetString() is { } text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
                ? value.ToUniversalTime()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sends a GET, authenticating on a challenge and following redirects; the caller checks the status.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        ImageName image, RegistryAuth? auth, Uri url, IReadOnlyList<string> accept, CancellationToken cancellationToken)
    {
        var key = $"{image.Registry}/{image.Repository}";
        var authorization = tokens.TryGetValue(key, out var cached) && cached.Expires > time.GetUtcNow() ? cached.Header : null;
        var response = await GetAsync(url, accept, authorization, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var challenge = response.Headers.WwwAuthenticate.FirstOrDefault();
        response.Dispose();
        authorization = challenge?.Scheme.ToLowerInvariant() switch
        {
            "bearer" => await TokenAsync(image, auth, challenge.Parameter ?? "", key, cancellationToken),
            "basic" when auth is not null => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}")),
            _ => null,
        };
        if (authorization is null)
        {
            throw new RegistryUnavailableException(
                $"{image.Registry} requires a login for {image}; set the registry's credentials for the stack's environment.");
        }

        return await GetAsync(url, accept, authorization, cancellationToken);
    }

    private async Task<string> TokenAsync(ImageName image, RegistryAuth? auth, string parameters, string key, CancellationToken cancellationToken)
    {
        var values = ChallengeParameter().Matches(parameters).ToDictionary(m => m.Groups["key"].Value.ToLowerInvariant(), m => m.Groups["value"].Value);
        if (!values.TryGetValue("realm", out var realmText) || !Uri.TryCreate(realmText, UriKind.Absolute, out var realm) || realm.Scheme != Uri.UriSchemeHttps)
        {
            throw new RegistryUnavailableException($"{image.Registry} asked for a token from a realm that is not an https:// address.");
        }

        var query = new List<string>();
        if (values.TryGetValue("service", out var service))
        {
            query.Add($"service={Uri.EscapeDataString(service)}");
        }

        query.Add($"scope={Uri.EscapeDataString(values.GetValueOrDefault("scope") ?? $"repository:{image.Repository}:pull")}");
        if (realm.Query.TrimStart('?') is { Length: > 0 } existing)
        {
            query.Insert(0, existing);
        }

        var url = new UriBuilder(realm) { Query = string.Join('&', query) }.Uri;

        // The login goes to the realm only when it is the registry's own host: a registry must not get it sent elsewhere.
        var basic = auth is not null && SameHost(realm, Base(image))
            ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}"))
            : auth is not null && image.Registry == RegistryHost.DockerHub && realm.Host == "auth.docker.io"
                ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}"))
                : null;
        using var response = await GetAsync(url, [], basic, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new RegistryUnavailableException(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? $"{image.Registry} refused the login for {image}; check the registry's credentials for the stack's environment."
                    : $"{image.Registry} did not give a token for {image} ({(int)response.StatusCode}).",
                rateLimited: response.StatusCode == HttpStatusCode.TooManyRequests);
        }

        var body = await ReadAsync(response, 64 * 1024, cancellationToken);
        try
        {
            using var json = JsonDocument.Parse(body);
            var token = (json.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null)
                ?? (json.RootElement.TryGetProperty("access_token", out var a) ? a.GetString() : null)
                ?? throw new RegistryUnavailableException($"{image.Registry} gave no token for {image}.");
            var lifetime = json.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 60;
            var header = "Bearer " + token;
            tokens[key] = (header, time.GetUtcNow().AddSeconds(Math.Clamp(lifetime - 10, 0, 3600)));
            return header;
        }
        catch (JsonException)
        {
            throw new RegistryUnavailableException($"{image.Registry} answered the token request with something that is not JSON.");
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri url, IReadOnlyList<string> accept, string? authorization, CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            foreach (var type in accept)
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
            }

            // Only to the host the request started at: a blob redirect to storage must not carry the registry's token.
            if (authorization is not null && SameHost(current, url))
            {
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RegistryTimeout);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RegistryUnavailableException($"{current.Host} did not answer in {options.RegistryTimeout.TotalSeconds:0} s.");
            }
            catch (HttpRequestException ex)
            {
                LogUnreachable(current.Host, ex.Message);
                throw new RegistryUnavailableException(ex.InnerException is PrivateAddressException blocked
                    ? blocked.Message
                    : $"{current.Host} could not be reached.");
            }

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (hop >= MaxRedirects)
                {
                    throw new RegistryUnavailableException($"{url.Host} redirected too many times.");
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (current.Scheme != Uri.UriSchemeHttps)
                {
                    throw new RegistryUnavailableException($"{url.Host} redirected to an address that is not https://.");
                }

                continue;
            }

            return response;
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, ImageName image, string? reference)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var what = reference is null ? image.Value : $"{image}:{reference}";
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new RegistryNotFoundException($"{image.Registry} has no {what}."),
            HttpStatusCode.TooManyRequests => new RegistryUnavailableException($"{image.Registry} limits requests (429) for {what}.", rateLimited: true),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new RegistryUnavailableException(
                $"{image.Registry} refused access to {what}; set the registry's credentials for the stack's environment."),
            _ => new RegistryUnavailableException($"{image.Registry} answered {(int)response.StatusCode} for {what}."),
        };
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > limit)
        {
            throw new RegistryUnavailableException($"The registry's answer is larger than {limit / 1024} KiB.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new RegistryUnavailableException($"The registry's answer is larger than {limit / 1024} KiB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static Uri? NextPage(HttpResponseMessage response, Uri current)
    {
        if (!response.Headers.TryGetValues("Link", out var links))
        {
            return null;
        }

        foreach (var link in links)
        {
            var match = LinkNext().Match(link);
            if (match.Success && Uri.TryCreate(current, match.Groups["url"].Value, out var next) && SameHost(next, current))
            {
                return next;
            }
        }

        return null;
    }

    private static Uri Base(ImageName image) =>
        new($"https://{(image.Registry == RegistryHost.DockerHub ? "registry-1.docker.io" : image.Registry)}/v2/");

    private static bool SameHost(Uri a, Uri b) => string.Equals(a.Authority, b.Authority, StringComparison.OrdinalIgnoreCase);

    internal static SocketsHttpHandler CreateHandler(ImageUpdateOptions options) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = IPAddress.TryParse(context.DnsEndPoint.Host.Trim('[', ']'), out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            if (!options.AllowPrivateRegistries && (addresses.Length == 0 || addresses.Any(a => !GitHostGuard.IsPublic(a))))
            {
                throw new PrivateAddressException(
                    $"Registry host {context.DnsEndPoint.Host} is on a private network; the aggregator setting " +
                    "ImageUpdates:AllowPrivateRegistries allows it.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // The checked addresses themselves, so a second lookup cannot answer differently.
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private sealed class PrivateAddressException(string message) : Exception(message);

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}\z")]
    private static partial Regex DigestPattern();

    [GeneratedRegex(@"(?<key>[A-Za-z]+)=""(?<value>[^""]*)""")]
    private static partial Regex ChallengeParameter();

    [GeneratedRegex(@"<(?<url>[^>]+)>\s*;\s*rel=""?next""?")]
    private static partial Regex LinkNext();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Registry {Host} could not be reached: {Reason}")]
    private partial void LogUnreachable(string host, string reason);
}
