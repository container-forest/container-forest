using System.Net;
using System.Text;
using ContainerForest.Aggregator.Application.Images;
using ContainerForest.Aggregator.Domain.Images;
using ContainerForest.Aggregator.Infrastructure.Images;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Images;

public class OciRegistryClientTests
{
    private static readonly ImageName Image = ImageName.Parse("reg.example.com/team/app");
    private static readonly RegistryAuth Login = new("ci", "s3cret");
    private static readonly string IndexDigest = "sha256:" + new string('1', 64);
    private static readonly string PlatformDigest = "sha256:" + new string('2', 64);
    private static readonly string ConfigDigest = "sha256:" + new string('3', 64);

    private readonly FakeRegistry registry = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_tags_with_a_bearer_token_across_pages()
    {
        registry.RequireToken = true;
        registry.Routes["/v2/team/app/tags/list?n=1000"] = _ => Json("""{"tags":["a","b"]}""", link: "</v2/team/app/tags/list?n=1000&last=b>; rel=\"next\"");
        registry.Routes["/v2/team/app/tags/list?n=1000&last=b"] = _ => Json("""{"tags":["c"]}""");

        var tags = await Client().ListTagsAsync(Image, Login, Ct);

        Assert.Equal(["a", "b", "c"], tags);
        Assert.Equal(1, registry.TokenRequests);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ci:s3cret")), registry.TokenAuthorization);
        Assert.Contains("scope=repository%3Ateam%2Fapp%3Apull", registry.TokenQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_the_created_time_of_a_multi_platform_image_without_sending_the_token_to_storage()
    {
        registry.RequireToken = true;
        registry.Routes["/v2/team/app/manifests/main-1"] = _ => Json(
            $$$"""{"mediaType":"application/vnd.oci.image.index.v1+json","manifests":[{"digest":"sha256:{{{new string('9', 64)}}}","platform":{"os":"unknown","architecture":"unknown"}},{"digest":"{{{PlatformDigest}}}","platform":{"os":"linux","architecture":"arm64"}}]}""",
            mediaType: "application/vnd.oci.image.index.v1+json",
            digest: IndexDigest);
        registry.Routes[$"/v2/team/app/manifests/{PlatformDigest}"] = _ => Json(
            $$$"""{"mediaType":"application/vnd.oci.image.manifest.v1+json","config":{"digest":"{{{ConfigDigest}}}"}}""",
            mediaType: "application/vnd.oci.image.manifest.v1+json");
        registry.Routes[$"/v2/team/app/blobs/{ConfigDigest}"] = _ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            redirect.Headers.Location = new Uri("https://storage.example.com/blob/abc");
            return redirect;
        };
        registry.Routes["https://storage.example.com/blob/abc"] = request =>
        {
            registry.StorageAuthorization = request.Headers.Authorization?.ToString();
            return Json("""{"created":"2026-11-09T12:00:00Z","architecture":"arm64"}""");
        };

        var image = await Client().GetImageAsync(Image, "main-1", Login, readCreated: true, Ct);

        Assert.Equal(IndexDigest, image.Digest);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), image.Created);
        Assert.Null(registry.StorageAuthorization);
    }

    [Fact]
    public async Task Rate_limit_and_missing_tag_are_told_apart()
    {
        registry.Routes["/v2/team/app/manifests/busy"] = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        registry.Routes["/v2/team/app/manifests/gone"] = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        var limited = await Assert.ThrowsAsync<RegistryUnavailableException>(() => Client().GetImageAsync(Image, "busy", null, false, Ct));
        await Assert.ThrowsAsync<RegistryNotFoundException>(() => Client().GetImageAsync(Image, "gone", null, false, Ct));

        Assert.True(limited.RateLimited);
    }

    [Fact]
    public async Task Digest_is_computed_when_the_registry_does_not_send_it()
    {
        const string body = """{"mediaType":"application/vnd.oci.image.manifest.v1+json","config":{"digest":"sha256:3333333333333333333333333333333333333333333333333333333333333333"}}""";
        registry.Routes["/v2/team/app/manifests/v1"] = _ => Json(body, mediaType: "application/vnd.oci.image.manifest.v1+json");

        var image = await Client().GetImageAsync(Image, "v1", null, readCreated: false, Ct);

        Assert.Equal("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body))), image.Digest);
    }

    [Fact]
    public async Task Private_login_is_not_sent_to_a_token_realm_on_another_host()
    {
        registry.RequireToken = true;
        registry.Realm = "https://auth.elsewhere.example/token";
        registry.Routes["/v2/team/app/tags/list?n=1000"] = _ => Json("""{"tags":[]}""");

        await Client().ListTagsAsync(Image, Login, Ct);

        Assert.Null(registry.TokenAuthorization);
    }

    private OciRegistryClient Client() =>
        new(registry, new ImageUpdateOptions(), TimeProvider.System, NullLogger<OciRegistryClient>.Instance);

    private static HttpResponseMessage Json(string body, string? link = null, string mediaType = "application/json", string? digest = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        if (link is not null)
        {
            response.Headers.Add("Link", link);
        }

        if (digest is not null)
        {
            response.Headers.Add("Docker-Content-Digest", digest);
        }

        return response;
    }

    /// <summary>reg.example.com with an optional token service: answers by path (and query), or by full URL for other hosts.</summary>
    private sealed class FakeRegistry : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = [];

        public bool RequireToken { get; set; }

        public string Realm { get; set; } = "https://reg.example.com/token";

        public int TokenRequests { get; private set; }

        public string? TokenAuthorization { get; private set; }

        public string TokenQuery { get; private set; } = "";

        public string? StorageAuthorization { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            if (url.GetLeftPart(UriPartial.Path) == Realm)
            {
                TokenRequests++;
                TokenAuthorization = request.Headers.Authorization?.ToString();
                TokenQuery = url.Query;
                return Task.FromResult(Json("""{"token":"t1","expires_in":300}"""));
            }

            if (url.Host == "reg.example.com" && RequireToken && request.Headers.Authorization?.ToString() != "Bearer t1")
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.ParseAdd($"Bearer realm=\"{Realm}\",service=\"reg\",scope=\"repository:team/app:pull\"");
                return Task.FromResult(challenge);
            }

            var key = url.Host == "reg.example.com" ? url.PathAndQuery : url.ToString();
            return Task.FromResult(Routes.TryGetValue(key, out var route) ? route(request) : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
