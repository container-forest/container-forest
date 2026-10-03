using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Application.Webhooks;

/// <summary>One delivery as it arrived: its headers and the exact bytes of its body, which the signature covers.</summary>
/// <param name="Headers">Looked up case-insensitively.</param>
public sealed record WebhookDelivery(IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

public enum WebhookEventKind
{
    /// <summary>Sent once when the webhook is created, to check it works.</summary>
    Ping,
    Push,

    /// <summary>Any other event; it is acknowledged and ignored.</summary>
    Other,
}

/// <summary>A push as the git server described it.</summary>
/// <param name="Branch">Null when the push was to a tag or another non-branch ref.</param>
/// <param name="After">The ref's new commit; null when the branch was deleted.</param>
public sealed record PushEvent(BranchName? Branch, CommitSha? After);

/// <summary>The body is not the JSON the git server should have sent.</summary>
public sealed class WebhookPayloadException(string message) : Exception(message);

/// <summary>
/// A git server that sends webhooks: how to recognize its deliveries, check they come from it and read a push.
/// Each server signs differently, so the checking is the polymorphic part; reading a push is shared, because GitHub,
/// Gitea and GitLab all describe it with "ref" and "after".
/// </summary>
public abstract class GitWebhookProvider
{
    private const string BranchPrefix = "refs/heads/";

    /// <summary>For logs and the delivery response.</summary>
    public abstract string Name { get; }

    /// <summary>True when the delivery carries this server's event header.</summary>
    public abstract bool Recognizes(WebhookDelivery delivery);

    /// <summary>True when the delivery was made with <paramref name="secret"/>. Compares in constant time.</summary>
    public abstract bool IsAuthentic(WebhookDelivery delivery, string secret);

    public abstract WebhookEventKind EventKind(WebhookDelivery delivery);

    /// <exception cref="WebhookPayloadException">The body is not a push payload.</exception>
    public virtual PushEvent ReadPush(WebhookDelivery delivery)
    {
        string? gitRef;
        string? after;
        try
        {
            using var json = JsonDocument.Parse(delivery.Body);
            var root = json.RootElement;
            gitRef = root.TryGetProperty("ref", out var refElement) ? refElement.GetString() : null;
            after = root.TryGetProperty("after", out var afterElement) ? afterElement.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new WebhookPayloadException("The body is not JSON; set the webhook's content type to application/json.");
        }

        if (gitRef is null || after is null)
        {
            throw new WebhookPayloadException("The push payload has no \"ref\" or \"after\".");
        }

        var branch = gitRef.StartsWith(BranchPrefix, StringComparison.Ordinal)
            && BranchName.TryParse(gitRef[BranchPrefix.Length..], out var parsed)
                ? parsed
                : null;

        // A deleted branch moves to the all-zero commit.
        var commit = CommitSha.TryParse(after, out var sha) && sha.Value.Any(c => c != '0') ? sha : null;
        return new PushEvent(branch, commit);
    }

    /// <summary>Compares two strings in constant time; hashing first hides their lengths as well.</summary>
    protected static bool FixedTimeEquals(string actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

/// <summary>A server that signs the body with HMAC-SHA256 of the secret and sends the hex digest in a header.</summary>
public abstract class HmacWebhookProvider : GitWebhookProvider
{
    /// <summary>Header with the signature.</summary>
    protected abstract string SignatureHeader { get; }

    /// <summary>Text before the hex digest in <see cref="SignatureHeader"/>, e.g. "sha256=".</summary>
    protected virtual string SignaturePrefix => "";

    public override bool IsAuthentic(WebhookDelivery delivery, string secret)
    {
        var signature = delivery.Header(SignatureHeader);
        if (signature is null || !signature.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), delivery.Body.Span));
        return FixedTimeEquals(signature[SignaturePrefix.Length..].ToLowerInvariant(), expected);
    }
}

/// <summary>GitHub: X-GitHub-Event and X-Hub-Signature-256 ("sha256=" + hex).</summary>
public sealed class GitHubWebhook : HmacWebhookProvider
{
    public override string Name => "GitHub";

    protected override string SignatureHeader => "X-Hub-Signature-256";

    protected override string SignaturePrefix => "sha256=";

    public override bool Recognizes(WebhookDelivery delivery) => delivery.Header("X-GitHub-Event") is not null;

    public override WebhookEventKind EventKind(WebhookDelivery delivery) => delivery.Header("X-GitHub-Event") switch
    {
        "ping" => WebhookEventKind.Ping,
        "push" => WebhookEventKind.Push,
        _ => WebhookEventKind.Other,
    };
}

/// <summary>
/// Gitea and Forgejo: X-Gitea-Event and X-Gitea-Signature (plain hex). They also send GitHub's headers, so this
/// provider is asked first.
/// </summary>
public sealed class GiteaWebhook : HmacWebhookProvider
{
    public override string Name => "Gitea";

    protected override string SignatureHeader => "X-Gitea-Signature";

    public override bool Recognizes(WebhookDelivery delivery) => delivery.Header("X-Gitea-Event") is not null;

    // Gitea has no ping event: its "Test delivery" sends a push.
    public override WebhookEventKind EventKind(WebhookDelivery delivery) =>
        delivery.Header("X-Gitea-Event") == "push" ? WebhookEventKind.Push : WebhookEventKind.Other;
}

/// <summary>GitLab: no signature, the secret itself comes back in X-Gitlab-Token; X-Gitlab-Event is "Push Hook".</summary>
public sealed class GitLabWebhook : GitWebhookProvider
{
    public override string Name => "GitLab";

    public override bool Recognizes(WebhookDelivery delivery) => delivery.Header("X-Gitlab-Event") is not null;

    public override bool IsAuthentic(WebhookDelivery delivery, string secret) =>
        delivery.Header("X-Gitlab-Token") is { } token && FixedTimeEquals(token, secret);

    public override WebhookEventKind EventKind(WebhookDelivery delivery) =>
        delivery.Header("X-Gitlab-Event") == "Push Hook" ? WebhookEventKind.Push : WebhookEventKind.Other;
}
