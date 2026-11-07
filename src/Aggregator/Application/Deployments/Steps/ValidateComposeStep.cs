using System.Text;
using ContainerForest.Aggregator.Domain.Deployments;

namespace ContainerForest.Aggregator.Application.Deployments.Steps;

/// <summary>
/// Checks the compose file before anything reaches the host. Rejects what the system does not support
/// (building images on hosts); warns about what makes a deployment hard to reproduce (floating "latest" images).
/// </summary>
public sealed class ValidateComposeStep(IComposeParser parser) : IDeployStep
{
    public string Name => "Validate compose file";

    public Task ExecuteAsync(DeployContext context, CancellationToken cancellationToken)
    {
        var composePath = context.Stack.ComposePath;
        var file = context.Source.Files.FirstOrDefault(f => f.Path.Value == composePath.FileName)
            ?? throw new DeploymentFailedException($"Compose file '{composePath}' does not exist at commit {context.Deployment.Commit?.Short}.");

        var compose = parser.Parse(Decode(file.Content, composePath.Value));
        if (compose.Services.Count == 0)
        {
            throw new DeploymentFailedException($"Compose file '{composePath}' defines no services.");
        }

        var errors = new List<string>();
        foreach (var service in compose.Services)
        {
            Check(service, errors, context);
        }

        if (!context.Policy.IsOff && !context.Stack.PolicyUnrestricted)
        {
            CheckForPolicy(compose, context, errors);
        }

        if (errors.Count > 0)
        {
            throw new DeploymentFailedException(string.Join(" ", errors));
        }

        context.Compose = compose;
        context.Info($"{compose.Services.Count} service(s): {string.Join(", ", compose.Services.Select(s => s.Name))}.");
        return Task.CompletedTask;
    }

    private static void Check(ComposeService service, List<string> errors, DeployContext context)
    {
        var requireDigest = context.Stack.RequireImageDigests;
        if (service.HasBuild)
        {
            errors.Add($"Service '{service.Name}' uses 'build', which is not supported: build the image in CI and set 'image'.");
            return;
        }

        if (service.Image is null)
        {
            // The base service of "extends" may supply the image; compose reports it if it does not.
            if (!service.Extends)
            {
                errors.Add($"Service '{service.Name}' has no 'image'.");
            }
            else if (requireDigest)
            {
                errors.Add($"Service '{service.Name}' takes its image from 'extends'; the stack requires every image to be pinned by digest in its own 'image'.");
            }

            return;
        }

        // "${REGISTRY}/app:${TAG}" is known only after interpolation on the host.
        if (service.Image.Contains('$', StringComparison.Ordinal))
        {
            if (requireDigest)
            {
                errors.Add($"Service '{service.Name}' has image '{service.Image}' with variables; the stack requires every image to be pinned by digest.");
            }

            return;
        }

        if (!ImageReference.TryParse(service.Image, out var image))
        {
            errors.Add($"Service '{service.Name}' has an invalid image reference '{service.Image}'.");
        }
        else if (requireDigest && !image.IsPinned)
        {
            // A signed commit says which tag, not which image: whoever can push to the registry could swap it.
            errors.Add($"Service '{service.Name}': image '{image}' has no digest; the stack requires images pinned as 'name@sha256:…'.");
        }
        else if (image.IsFloatingLatest)
        {
            var tag = image.Tag is null ? "has no tag (Docker uses 'latest')" : "uses the tag 'latest'";
            context.Warn(
                $"Service '{service.Name}': image '{image}' {tag}. Rolling back this stack will not bring back " +
                $"the previous image; pin a version tag or a digest to make deployments reproducible.");
        }
    }

    /// <summary>
    /// What compose would read before the agent's render can show it (ADR-0012): files outside the bundle, which
    /// `docker compose config` would read from the agent's file system (other stacks, the secrets tmpfs) or fetch from
    /// git and OCI registries, and YAML tags that change merging. The rendered model no longer shows any of them.
    /// </summary>
    private static void CheckForPolicy(ComposeFile compose, DeployContext context, List<string> errors)
    {
        if (compose.HasTags)
        {
            errors.Add("The compose file uses YAML tags (such as !reset or !override), which the compose policy does not allow.");
        }

        var bundle = context.Source.Files.Select(f => f.Path.Value).ToHashSet(StringComparer.Ordinal);
        var references = compose.Services
            .SelectMany(s => s.ReferencedFiles.Select(file => (Where: $"Service '{s.Name}'", File: file)))
            .Concat(compose.IncludedFiles.Select(file => (Where: "'include'", File: file)));
        foreach (var (where, file) in references)
        {
            if (BundlePath(file) is not { } path || !bundle.Contains(path))
            {
                errors.Add($"{where} reads '{file}', which is not a file of the stack's directory; under the compose policy " +
                    "compose may read only files deployed with the stack, named without variables.");
            }
        }
    }

    /// <summary>"./app.env" → "app.env"; null for an absolute path, a variable, a URL or a path that leaves the directory.</summary>
    private static string? BundlePath(string reference)
    {
        if (reference.Length == 0 || reference.Contains('$', StringComparison.Ordinal) || reference.StartsWith('/') || reference.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var segment in reference.Replace('\\', '/').Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    return null;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static string Decode(byte[] content, string path)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new DeploymentFailedException($"Compose file '{path}' is not valid UTF-8.");
        }
    }
}
