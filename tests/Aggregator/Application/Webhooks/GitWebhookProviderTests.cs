using System.Security.Cryptography;
using System.Text;
using ContainerForest.Aggregator.Application.Webhooks;

namespace ContainerForest.Aggregator.Application.Tests.Webhooks;

public class GitWebhookProviderTests
{
    private const string Secret = "It's a Secret to Everybody";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void GitHub_signature_matches_the_example_from_its_documentation()
    {
        // https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries#testing-the-webhook-payload-validation
        var delivery = Delivery(
            "Hello, World!",
            ("X-GitHub-Event", "push"),
            ("X-Hub-Signature-256", "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17"));

        Assert.True(new GitHubWebhook().IsAuthentic(delivery, Secret));
        Assert.False(new GitHubWebhook().IsAuthentic(delivery, "another secret"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    [InlineData("sha256=")]
    public void GitHub_without_a_valid_signature_is_not_authentic(string? signature)
    {
        var headers = signature is null
            ? new[] { ("X-GitHub-Event", "push") }
            : [("X-GitHub-Event", "push"), ("X-Hub-Signature-256", signature)];

        Assert.False(new GitHubWebhook().IsAuthentic(Delivery("Hello, World!", headers), Secret));
    }

    [Fact]
    public void A_changed_body_is_not_authentic()
    {
        var signature = Hmac("{\"ref\":\"refs/heads/main\"}");
        var delivery = Delivery("{\"ref\":\"refs/heads/prod\"}", ("X-Gitea-Event", "push"), ("X-Gitea-Signature", signature));

        Assert.False(new GiteaWebhook().IsAuthentic(delivery, Secret));
    }

    [Fact]
    public void Gitea_signs_with_plain_hex_in_upper_or_lower_case()
    {
        const string body = "{\"ref\":\"refs/heads/main\"}";

        Assert.True(new GiteaWebhook().IsAuthentic(Delivery(body, ("X-Gitea-Signature", Hmac(body))), Secret));
        Assert.True(new GiteaWebhook().IsAuthentic(Delivery(body, ("X-Gitea-Signature", Hmac(body).ToUpperInvariant())), Secret));
    }

    [Fact]
    public void GitLab_sends_the_secret_itself()
    {
        Assert.True(new GitLabWebhook().IsAuthentic(Delivery("{}", ("X-Gitlab-Token", Secret)), Secret));
        Assert.False(new GitLabWebhook().IsAuthentic(Delivery("{}", ("X-Gitlab-Token", Secret + "x")), Secret));
        Assert.False(new GitLabWebhook().IsAuthentic(Delivery("{}"), Secret));
    }

    [Fact]
    public void Each_provider_recognizes_its_own_event_header()
    {
        // Gitea sends GitHub's headers as well; the service asks the Gitea provider first.
        var gitea = Delivery("{}", ("X-Gitea-Event", "push"), ("X-GitHub-Event", "push"));

        Assert.True(new GiteaWebhook().Recognizes(gitea));
        Assert.False(new GitLabWebhook().Recognizes(gitea));
        Assert.True(new GitLabWebhook().Recognizes(Delivery("{}", ("X-Gitlab-Event", "Push Hook"))));
        Assert.False(new GitHubWebhook().Recognizes(Delivery("{}", ("X-Gitlab-Event", "Push Hook"))));
    }

    [Theory]
    [InlineData("ping", WebhookEventKind.Ping)]
    [InlineData("push", WebhookEventKind.Push)]
    [InlineData("pull_request", WebhookEventKind.Other)]
    public void GitHub_event_kinds(string header, WebhookEventKind kind) =>
        Assert.Equal(kind, new GitHubWebhook().EventKind(Delivery("{}", ("X-GitHub-Event", header))));

    [Fact]
    public void GitLab_and_Gitea_event_kinds()
    {
        Assert.Equal(WebhookEventKind.Push, new GitLabWebhook().EventKind(Delivery("{}", ("X-Gitlab-Event", "Push Hook"))));
        Assert.Equal(WebhookEventKind.Other, new GitLabWebhook().EventKind(Delivery("{}", ("X-Gitlab-Event", "Tag Push Hook"))));
        Assert.Equal(WebhookEventKind.Push, new GiteaWebhook().EventKind(Delivery("{}", ("X-Gitea-Event", "push"))));
        Assert.Equal(WebhookEventKind.Other, new GiteaWebhook().EventKind(Delivery("{}", ("X-Gitea-Event", "issues"))));
    }

    [Fact]
    public void Push_to_a_branch()
    {
        var push = new GitHubWebhook().ReadPush(Delivery($$"""{"ref":"refs/heads/release/1.2","after":"{{Sha.ToUpperInvariant()}}","commits":[]}"""));

        Assert.Equal("release/1.2", push.Branch?.Value);
        Assert.Equal(Sha, push.After?.Value);
    }

    [Theory]
    [InlineData("refs/tags/v1.0")]
    [InlineData("refs/heads/bad..name")]
    [InlineData("main")]
    public void Push_to_something_other_than_a_valid_branch_has_no_branch(string gitRef) =>
        Assert.Null(new GitHubWebhook().ReadPush(Delivery($$"""{"ref":"{{gitRef}}","after":"{{Sha}}"}""")).Branch);

    [Fact]
    public void Deleted_branch_has_no_new_commit() =>
        Assert.Null(new GitLabWebhook().ReadPush(Delivery($$"""{"ref":"refs/heads/main","after":"{{new string('0', 40)}}"}""")).After);

    [Theory]
    [InlineData("payload=%7B%7D")]
    [InlineData("[1, 2]")]
    [InlineData("{\"ref\":\"refs/heads/main\"}")]
    [InlineData("{\"ref\":1,\"after\":2}")]
    public void Body_that_is_not_a_push_payload_is_rejected(string body) =>
        Assert.Throws<WebhookPayloadException>(() => new GitHubWebhook().ReadPush(Delivery(body)));

    private static string Hmac(string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)));

    private static WebhookDelivery Delivery(string body, params (string Name, string Value)[] headers) => new(
        headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase), Encoding.UTF8.GetBytes(body));
}
