using ContainerForest.Aggregator.Api.Contracts;
using ContainerForest.Aggregator.Application.Images;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Images;

namespace ContainerForest.Aggregator.Api.Endpoints;

/// <summary>Image policies, the image hook and the aggregator's signing key (see ImageUpdateContracts).</summary>
internal static class ImageUpdateEndpoints
{
    public static IEndpointRouteBuilder MapImageUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .WithTags("Image updates")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/stacks/{id:guid}/image-policies", async (Guid id, ImagePolicyService service, CancellationToken ct) =>
                ToResponse(await service.ListAsync(StackId.From(id), ct)))
            .WithName("ListImagePolicies");

        api.MapPost(
                "/stacks/{id:guid}/image-policies",
                async (Guid id, CreateImagePolicyRequest request, ImagePolicyService service, CancellationToken ct) =>
                {
                    var policy = await service.AddAsync(
                        StackId.From(id),
                        request.Image,
                        new ImagePolicySettings(request.TagPattern, request.Order, request.Services ?? [], request.Poll, request.AcceptRequests, request.PinDigest),
                        ct);
                    return TypedResults.Created($"/api/image-policies/{policy.Id}", ToResponse(policy));
                })
            .WithName("AddImagePolicy")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut(
                "/image-policies/{policyId:guid}",
                async (Guid policyId, ImagePolicySettingsRequest request, ImagePolicyService service, CancellationToken ct) =>
                    ToResponse(await service.ChangeAsync(
                        ImagePolicyId.From(policyId),
                        new ImagePolicySettings(request.TagPattern, request.Order, request.Services ?? [], request.Poll, request.AcceptRequests, request.PinDigest),
                        ct)))
            .WithName("ChangeImagePolicy")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        api.MapDelete(
                "/image-policies/{policyId:guid}",
                async (Guid policyId, ImagePolicyService service, CancellationToken ct) =>
                {
                    await service.RemoveAsync(ImagePolicyId.From(policyId), ct);
                    return TypedResults.NoContent();
                })
            .WithName("RemoveImagePolicy");

        api.MapPost(
                "/image-policies/{policyId:guid}/check",
                async (Guid policyId, bool? dryRun, ImagePolicyService service, CancellationToken ct) =>
                    ToResponse(await service.CheckAsync(ImagePolicyId.From(policyId), dryRun ?? false, ct)))
            .WithName("CheckImagePolicy")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapPost(
                "/image-policies/{policyId:guid}/pin",
                async (Guid policyId, PinImageRequest request, ImagePolicyService service, CancellationToken ct) =>
                    ToResponse(await service.PinAsync(ImagePolicyId.From(policyId), request.Tag, ct)))
            .WithName("PinImage")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapPost(
                "/image-policies/{policyId:guid}/unpin",
                async (Guid policyId, ImagePolicyService service, CancellationToken ct) =>
                    ToResponse(await service.UnpinAsync(ImagePolicyId.From(policyId), ct)))
            .WithName("UnpinImage");

        api.MapGet("/system/signing-key", async (AggregatorSigningKeyService service, CancellationToken ct) =>
                (await service.ListAsync(ct)).Select(ToResponse).ToList())
            .WithName("ListAggregatorSigningKeys");

        api.MapPost("/system/signing-key/rotate", async (AggregatorSigningKeyService service, CancellationToken ct) =>
                ToResponse(await service.RotateAsync(ct)))
            .WithName("RotateAggregatorSigningKey");

        api.MapGet("/settings/image-updates", async (ImageUpdateSettingsService service, CancellationToken ct) =>
                ToResponse(await service.GetAsync(ct)))
            .WithName("GetImageUpdateSettings");

        api.MapPut(
                "/settings/image-updates",
                async (UpdateImageUpdateSettingsRequest request, ImageUpdateSettingsService service, CancellationToken ct) =>
                    ToResponse(await service.UpdateAsync(request.PollingEnabled, request.PollingIntervalMinutes, ct)))
            .WithName("UpdateImageUpdateSettings")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost("/api/image-hooks/{stackId:guid}", ReceiveImageHookAsync)
            .WithTags("Webhooks")
            .WithName("ReceiveImageHook")
            .AllowAnonymous()
            .Produces<IReadOnlyList<ImageUpdateResultResponse>>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> ReceiveImageHookAsync(
        Guid stackId, HttpRequest request, ImageHookService service, CancellationToken ct)
    {
        // The body is optional: an empty call checks every policy that accepts requests.
        ImageHookCallRequest? body = null;
        if (request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            if (request.ContentLength > 16 * 1024)
            {
                throw new BadHttpRequestException("The image hook body is too large.", StatusCodes.Status413PayloadTooLarge);
            }

            try
            {
                body = await request.ReadFromJsonAsync<ImageHookCallRequest>(ct);
            }
            catch (System.Text.Json.JsonException)
            {
                throw new DomainValidationException("The body must be JSON: {\"image\": \"…\", \"tag\": \"…\"}, both optional.");
            }
        }

        var results = await service.ReceiveAsync(StackId.From(stackId), BearerToken(request), body?.Image, body?.Tag, ct);
        return TypedResults.Accepted((string?)null, results.Select(ToResponse).ToList());
    }

    private static string? BearerToken(HttpRequest request)
    {
        const string scheme = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) && header.Length > scheme.Length
            ? header[scheme.Length..].Trim()
            : null;
    }

    private static ImagePolicyListResponse ToResponse(ImagePolicyList list) => new(
        list.Policies.Select(ToResponse).ToList(),
        list.CanChange,
        list.RecentUpdates.Select(r => new ImageUpdateRecordResponse(
            r.Id.Value,
            r.PolicyId.Value,
            r.Commit.Value,
            r.Parent.Value,
            r.Tag,
            r.Digest,
            r.Changes.Select(c => new ImageChangeResponse(c.Service, c.From, c.To)).ToList(),
            r.Reason,
            r.RequestedByName,
            r.CreatedAt)).ToList());

    private static ImagePolicyResponse ToResponse(ImagePolicy p) => new(
        p.Id.Value,
        p.StackId.Value,
        p.Image.Value,
        p.TagPattern,
        p.Order,
        p.Services,
        p.Poll,
        p.AcceptRequests,
        p.PinDigest,
        p.PinnedTag,
        p.PinnedBy,
        p.PinnedAt,
        p.LastCheckedAt,
        p.LastError,
        p.Warning,
        p.LatestTag,
        p.LastChangedByAdministrator,
        p.CreatedAt);

    private static ImageUpdateResultResponse ToResponse(ImageUpdateResult r) => new(
        r.PolicyId.Value, r.Image.Value, r.Outcome, r.CurrentTag, r.TargetTag, r.Detail, r.Commit?.Value, r.DeploymentId?.Value);

    private static AggregatorSigningKeyResponse ToResponse(AggregatorSigningKey k) =>
        new(k.Id.Value, k.PublicKey, k.Fingerprint, k.CreatedAt, k.RetiredAt);

    private static ImageUpdateSettingsResponse ToResponse(ResolvedSettings settings)
    {
        var typed = ImageUpdateSettings.From(settings);
        return new ImageUpdateSettingsResponse(
            typed.PollingEnabled,
            (int)typed.PollingInterval.TotalMinutes,
            settings[ImageUpdateSettingsSection.PollingEnabledField].IsLocked,
            settings[ImageUpdateSettingsSection.PollingIntervalMinutesField].IsLocked);
    }
}
