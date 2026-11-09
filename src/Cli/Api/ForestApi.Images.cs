using ContainerForest.Cli.Api.Generated.Models;

namespace ContainerForest.Cli.Api;

public sealed partial class ForestApi
{
    public async Task<ImagePolicyListResponse> ListImagePoliciesAsync(Guid stackId, CancellationToken cancellationToken) =>
        Required(await Api.Stacks[stackId].ImagePolicies.GetAsync(cancellationToken: cancellationToken), "image policies");

    /// <summary>The stack's policy for the image, given as it is written in a compose file.</summary>
    /// <exception cref="CliException">The stack has no policy for the image.</exception>
    public async Task<ImagePolicyResponse> FindImagePolicyAsync(Guid stackId, string image, CancellationToken cancellationToken)
    {
        var policies = (await ListImagePoliciesAsync(stackId, cancellationToken)).Policies ?? [];
        var wanted = ImageNames.Normalize(image);
        return policies.FirstOrDefault(p => p.Image == wanted || p.Id?.ToString() == image)
            ?? throw new CliException($"The stack has no image policy for {image}; see 'forest stack image-policy list'.", ExitCodes.Denied);
    }

    public async Task<ImagePolicyResponse> AddImagePolicyAsync(Guid stackId, CreateImagePolicyRequest request, CancellationToken cancellationToken) =>
        Required(await Api.Stacks[stackId].ImagePolicies.PostAsync(request, cancellationToken: cancellationToken), "image policy");

    public async Task<ImagePolicyResponse> ChangeImagePolicyAsync(Guid policyId, ImagePolicySettingsRequest request, CancellationToken cancellationToken) =>
        Required(await Api.ImagePolicies[policyId].PutAsync(request, cancellationToken: cancellationToken), "image policy");

    public async Task RemoveImagePolicyAsync(Guid policyId, CancellationToken cancellationToken) =>
        await Api.ImagePolicies[policyId].DeleteAsync(cancellationToken: cancellationToken);

    public async Task<ImageUpdateResultResponse> CheckImagePolicyAsync(Guid policyId, bool dryRun, CancellationToken cancellationToken) =>
        Required(
            await Api.ImagePolicies[policyId].Check.PostAsync(c => c.QueryParameters.DryRun = dryRun, cancellationToken),
            "image check");

    public async Task<ImageUpdateResultResponse> PinImageAsync(Guid policyId, string tag, CancellationToken cancellationToken) =>
        Required(
            await Api.ImagePolicies[policyId].Pin.PostAsync(new PinImageRequest { Tag = tag }, cancellationToken: cancellationToken),
            "image pin");

    public async Task<ImagePolicyResponse> UnpinImageAsync(Guid policyId, CancellationToken cancellationToken) =>
        Required(await Api.ImagePolicies[policyId].Unpin.PostAsync(cancellationToken: cancellationToken), "image policy");

    public async Task<ImageHookTokenResponse> CreateImageHookAsync(Guid stackId, bool allowDowngrade, CancellationToken cancellationToken) =>
        Required(
            await Api.Stacks[stackId].ImageHook.PostAsync(new ImageHookRequest { AllowDowngrade = allowDowngrade }, cancellationToken: cancellationToken),
            "image hook");

    public async Task<StackResponse> SetImageHookDowngradeAsync(Guid stackId, bool allowDowngrade, CancellationToken cancellationToken) =>
        Required(
            await Api.Stacks[stackId].ImageHook.PutAsync(new ImageHookRequest { AllowDowngrade = allowDowngrade }, cancellationToken: cancellationToken),
            "stack");

    public async Task RemoveImageHookAsync(Guid stackId, CancellationToken cancellationToken) =>
        await Api.Stacks[stackId].ImageHook.DeleteAsync(cancellationToken: cancellationToken);

    public async Task<GitConnectionResponse> SetGitConnectionPushTokenAsync(Guid id, string? userName, string token, CancellationToken cancellationToken) =>
        Required(
            await Api.GitConnections[id].PushCredential.PutAsync(
                new GitAccessTokenRequest { UserName = userName, Token = token }, cancellationToken: cancellationToken),
            "git connection");

    public async Task RemoveGitConnectionPushTokenAsync(Guid id, CancellationToken cancellationToken) =>
        await Api.GitConnections[id].PushCredential.DeleteAsync(cancellationToken: cancellationToken);

    /// <summary>The aggregator's signing keys, newest first; the active one has no retirement time.</summary>
    public async Task<IReadOnlyList<AggregatorSigningKeyResponse>> ListAggregatorSigningKeysAsync(CancellationToken cancellationToken) =>
        await Api.System.SigningKey.GetAsync(cancellationToken: cancellationToken) ?? [];

    public async Task<AggregatorSigningKeyResponse> RotateAggregatorSigningKeyAsync(CancellationToken cancellationToken) =>
        Required(await Api.System.SigningKey.Rotate.PostAsync(cancellationToken: cancellationToken), "signing key");
}

/// <summary>Image names as the aggregator compares them, so "nginx" finds the policy for "docker.io/library/nginx".</summary>
internal static class ImageNames
{
    public static string Normalize(string image)
    {
        var name = image.Trim();
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        var first = slash < 0 ? "" : name[..slash];
        var hasHost = first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal) || first == "localhost";
        var (host, path) = hasHost ? (first.ToLowerInvariant(), name[(slash + 1)..]) : ("docker.io", name);
        if (host is "index.docker.io" or "registry-1.docker.io" or "registry.hub.docker.com")
        {
            host = "docker.io";
        }

        if (host == "docker.io" && !path.Contains('/', StringComparison.Ordinal))
        {
            path = "library/" + path;
        }

        return $"{host}/{path}";
    }
}
