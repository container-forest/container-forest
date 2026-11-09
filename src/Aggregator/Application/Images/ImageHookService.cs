using ContainerForest.Aggregator.Application.Stacks;
using ContainerForest.Aggregator.Application.Webhooks;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Images;
using Microsoft.Extensions.Logging;

namespace ContainerForest.Aggregator.Application.Images;

/// <summary>The stack has no policy for the image that accepts requests; said the same way as for an image it lacks.</summary>
public sealed class ImageHookNotFoundException() : Exception("The stack has no image policy that accepts requests for this image.");

/// <summary>
/// Receives calls to a stack's image hook (ADR-0015, item 2a): CI built an image and asks the aggregator to check the
/// stack's image policies now, or to update one of them to an exact tag. Anonymous: the stack's token is the
/// authentication, and only its hash is stored. Not a user action, like a push: the stack's Editors created the hook
/// and the policies; the call only says when, and the policies say what.
/// </summary>
public sealed partial class ImageHookService(
    IStackRepository stacks,
    IImagePolicyRepository policies,
    ImageUpdateService updates,
    ILogger<ImageHookService> logger)
{
    /// <param name="token">The token the caller presented; null when it presented none.</param>
    /// <param name="image">Only the policy of this image; null for every policy that accepts requests.</param>
    /// <param name="tag">An exact tag; needs <paramref name="image"/>.</param>
    /// <exception cref="WebhookRejectedException">No such stack, no image hook, or a wrong token: one answer for all.</exception>
    /// <exception cref="ImageHookNotFoundException">No policy of the image accepts requests.</exception>
    /// <exception cref="DomainValidationException">A tag without an image, or one the policy does not allow.</exception>
    public async Task<IReadOnlyList<ImageUpdateResult>> ReceiveAsync(
        StackId stackId, string? token, string? image, string? tag, CancellationToken cancellationToken)
    {
        var stack = await stacks.FindAsync(stackId, cancellationToken);
        if (stack is null || token is null || !stack.AcceptsImageHook(token))
        {
            if (stack?.HasImageHook == true)
            {
                LogRejected(stack.Name.Value);
            }

            throw new WebhookRejectedException();
        }

        if (tag is not null && image is null)
        {
            throw new DomainValidationException("A tag is asked for with its image: {\"image\": \"…\", \"tag\": \"…\"}.");
        }

        var accepting = (await policies.ListByStackAsync(stack.Id, cancellationToken)).Where(p => p.AcceptRequests).ToList();
        if (image is not null)
        {
            ImageName name;
            try
            {
                name = ImageName.Parse(image);
            }
            catch (DomainValidationException)
            {
                throw new ImageHookNotFoundException();
            }

            accepting = accepting.Where(p => p.Image == name).ToList();
            if (accepting.Count == 0)
            {
                throw new ImageHookNotFoundException();
            }
        }

        var results = new List<ImageUpdateResult>();
        foreach (var policy in accepting)
        {
            results.Add(await updates.CheckAsync(
                policy.Id,
                new ImageUpdateRequest(ImageUpdateSource.Hook, tag?.Trim(), AllowDowngrade: stack.ImageHookAllowsDowngrade),
                cancellationToken));
        }

        LogReceived(stack.Name.Value, results.Count);
        return results;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected an image hook call for stack {Stack}: missing or wrong token")]
    private partial void LogRejected(string stack);

    [LoggerMessage(Level = LogLevel.Information, Message = "Image hook of stack {Stack} checked {Count} policies")]
    private partial void LogReceived(string stack, int count);
}
