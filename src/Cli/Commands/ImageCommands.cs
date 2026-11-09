using System.CommandLine;
using ContainerForest.Cli.Api;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

/// <summary>A command about one image policy of a stack: <c>forest stack image-policy &lt;verb&gt; STACK IMAGE</c>.</summary>
internal abstract class ImagePolicyCommand(string name, string description) : StackCommand(name, description)
{
    private readonly Argument<string> image = new("image") { Description = "The image the policy updates, as in the compose file without a tag." };

    protected sealed override void ConfigureStack(Command command)
    {
        command.Add(image);
        ConfigurePolicy(command);
    }

    protected virtual void ConfigurePolicy(Command command)
    {
    }

    protected sealed override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack) =>
        await ExecuteAsync(context, stack, await context.Api.FindImagePolicyAsync(stack.Id!.Value, context.Value(image)!, context.CancellationToken));

    protected abstract Task<int> ExecuteAsync(CommandContext context, StackResponse stack, ImagePolicyResponse policy);

    public static async Task ShowResultAsync(CommandContext context, ImageUpdateResultResponse result)
    {
        if (context.Output.IsJson)
        {
            await context.Output.ItemAsync(result, _ => []);
            return;
        }

        var text = result.Outcome switch
        {
            ImageUpdateOutcome.Updated => $"Updated {result.Image} from {result.CurrentTag} to {result.TargetTag} in commit {CliOutput.ShortCommit(result.Commit)}.",
            ImageUpdateOutcome.WouldUpdate => $"Would update {result.Image}: {result.Detail}.",
            ImageUpdateOutcome.UpToDate => $"{result.Image} is up to date at {result.TargetTag}.",
            ImageUpdateOutcome.Pinned => result.Detail ?? $"{result.Image} is pinned.",
            ImageUpdateOutcome.Waiting => $"Nothing to update yet: {result.Detail}",
            _ => null,
        };
        if (text is null)
        {
            throw new CliException($"The image check failed: {result.Detail}");
        }

        context.Output.Done(text);
    }
}

/// <summary>The options of a policy, for <c>add</c> and <c>change</c>.</summary>
internal sealed class ImagePolicyOptions
{
    public Option<string> TagPattern { get; } = new("--tag-pattern")
    {
        Description = @"A regular expression a tag must match whole, from ^ to \z, e.g. ^main-[0-9a-f]{7}\z.",
        Required = true,
    };

    public Option<string> Order { get; } = new("--order")
    {
        Description = "How to tell the newest tag: created (the image's creation time) or timestamp (the named group ts of the pattern).",
        DefaultValueFactory = _ => "created",
    };

    public Option<string[]> Services { get; } = new("--service")
    {
        Description = "Only this compose service; repeat for more. Leave out for every service with the image.",
        AllowMultipleArgumentsPerToken = false,
    };

    public Option<bool> NoPoll { get; } = new("--no-poll") { Description = "Do not read the registry on a schedule; only the image hook and people update." };

    public Option<bool> NoRequests { get; } = new("--no-requests") { Description = "The stack's image hook may not ask for this image." };

    public Option<bool> PinDigest { get; } = new("--pin-digest") { Description = "Write tag@sha256:…, as stacks that require image digests need." };

    public void AddTo(Command command)
    {
        Order.AcceptOnlyFromAmong("created", "timestamp");
        command.Add(TagPattern);
        command.Add(Order);
        command.Add(Services);
        command.Add(NoPoll);
        command.Add(NoRequests);
        command.Add(PinDigest);
    }

    public ImageTagOrder OrderOf(CommandContext context) =>
        context.Value(Order) == "timestamp" ? ImageTagOrder.TagTimestamp : ImageTagOrder.ImageCreated;
}

internal sealed class ImagePolicyListCommand() : StackCommand("list", "List the stack's image policies and their latest updates.")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        var list = await context.Api.ListImagePoliciesAsync(stack.Id!.Value, context.CancellationToken);
        await context.Output.ListAsync(
            list.Policies ?? [],
            items =>
            {
                var table = new Table("IMAGE", "PATTERN", "ORDER", "TRIGGERS", "LATEST", "STATE");
                foreach (var p in items)
                {
                    var triggers = string.Join(",", new[] { p.Poll == true ? "poll" : null, p.AcceptRequests == true ? "hook" : null }.OfType<string>());
                    var state = p.PinnedTag is not null ? $"pinned at {p.PinnedTag} by {p.PinnedBy}"
                        : p.LastError is not null ? $"failed: {p.LastError}"
                        : p.LastCheckedAt is null ? "not checked" : $"checked {CliOutput.Time(p.LastCheckedAt)}";
                    table.AddRow(p.Image, p.TagPattern, p.Order == ImageTagOrder.TagTimestamp ? "timestamp" : "created", triggers.Length == 0 ? "-" : triggers, p.LatestTag ?? "-", state);
                }

                return table;
            },
            $"{stack.Name} has no image policies.");
        return ExitCodes.Ok;
    }
}

internal sealed class ImagePolicyAddCommand() : StackCommand("add", "Let the aggregator update an image of the stack (Editor).")
{
    private readonly Argument<string> image = new("image") { Description = "The image without a tag, e.g. gitea.example.com/team/app/app." };
    private readonly ImagePolicyOptions options = new();

    protected override void ConfigureStack(Command command)
    {
        command.Add(image);
        options.AddTo(command);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        var policy = await context.Api.AddImagePolicyAsync(
            stack.Id!.Value,
            new CreateImagePolicyRequest
            {
                Image = context.Value(image),
                TagPattern = context.Value(options.TagPattern),
                Order = options.OrderOf(context),
                Services = [.. context.Value(options.Services) ?? []],
                Poll = !context.Value(options.NoPoll),
                AcceptRequests = !context.Value(options.NoRequests),
                PinDigest = context.Value(options.PinDigest),
            },
            context.CancellationToken);
        context.Output.Done($"The aggregator updates {policy.Image} in {stack.Name}; 'forest stack image-policy check --dry-run' shows what it would do now.");
        return ExitCodes.Ok;
    }
}

internal sealed class ImagePolicyChangeCommand() : ImagePolicyCommand("change", "Change an image policy; every option is set anew (Editor).")
{
    private readonly ImagePolicyOptions options = new();

    protected override void ConfigurePolicy(Command command) => options.AddTo(command);

    protected override async Task<int> ExecuteAsync(CommandContext context, StackResponse stack, ImagePolicyResponse policy)
    {
        await context.Api.ChangeImagePolicyAsync(
            policy.Id!.Value,
            new ImagePolicySettingsRequest
            {
                TagPattern = context.Value(options.TagPattern),
                Order = options.OrderOf(context),
                Services = [.. context.Value(options.Services) ?? []],
                Poll = !context.Value(options.NoPoll),
                AcceptRequests = !context.Value(options.NoRequests),
                PinDigest = context.Value(options.PinDigest),
            },
            context.CancellationToken);
        context.Output.Done($"Policy for {policy.Image} changed.");
        return ExitCodes.Ok;
    }
}

internal sealed class ImagePolicyRemoveCommand() : ImagePolicyCommand("remove", "Stop updating the image; its tag stays as it is (Editor).")
{
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void ConfigurePolicy(Command command) => command.Add(yes);

    protected override async Task<int> ExecuteAsync(CommandContext context, StackResponse stack, ImagePolicyResponse policy)
    {
        Confirmation.Require(context, yes, $"stop updating {policy.Image} in {stack.Name}");
        await context.Api.RemoveImagePolicyAsync(policy.Id!.Value, context.CancellationToken);
        context.Output.Done($"Policy for {policy.Image} removed.");
        return ExitCodes.Ok;
    }
}

internal sealed class ImagePolicyCheckCommand() : ImagePolicyCommand("check", "Check the registry now and update the image if a newer tag is allowed (Editor; --dry-run for anyone).")
{
    private readonly Option<bool> dryRun = new("--dry-run") { Description = "Only show what would change." };

    protected override void ConfigurePolicy(Command command) => command.Add(dryRun);

    protected override async Task<int> ExecuteAsync(CommandContext context, StackResponse stack, ImagePolicyResponse policy)
    {
        await ShowResultAsync(context, await context.Api.CheckImagePolicyAsync(policy.Id!.Value, context.Value(dryRun), context.CancellationToken));
        return ExitCodes.Ok;
    }
}

internal sealed class ImagePinCommand() : ImagePolicyCommand("pin", "Update the image to a tag, older or newer, and hold it there (Editor).")
{
    private readonly Argument<string> tag = new("tag") { Description = "A tag the policy allows." };

    protected override void ConfigurePolicy(Command command) => command.Add(tag);

    protected override async Task<int> ExecuteAsync(CommandContext context, StackResponse stack, ImagePolicyResponse policy)
    {
        await ShowResultAsync(context, await context.Api.PinImageAsync(policy.Id!.Value, context.Value(tag)!, context.CancellationToken));
        context.Output.Note($"Automatic updates of {policy.Image} stop until 'forest stack image-policy unpin'.");
        return ExitCodes.Ok;
    }
}

internal sealed class ImageUnpinCommand() : ImagePolicyCommand("unpin", "Resume automatic updates of the image (Editor).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, StackResponse stack, ImagePolicyResponse policy)
    {
        await context.Api.UnpinImageAsync(policy.Id!.Value, context.CancellationToken);
        context.Output.Done($"{policy.Image} is unpinned; the next check takes the newest tag.");
        return ExitCodes.Ok;
    }
}

internal sealed class ImageHookCreateCommand() : StackCommand("create", "Create the stack's image hook for CI; a new one replaces the old (Editor).")
{
    private readonly Option<bool> allowDowngrade = new("--allow-downgrade") { Description = "The hook may ask for an older tag, which pins the image there." };

    protected override void ConfigureStack(Command command) => command.Add(allowDowngrade);

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        var hook = await context.Api.CreateImageHookAsync(stack.Id!.Value, context.Value(allowDowngrade), context.CancellationToken);
        var url = new Uri(context.Api.Server, $"api/image-hooks/{stack.Id}");
        if (context.Output.IsJson)
        {
            await context.Output.ItemAsync(hook, _ => []);
            return ExitCodes.Ok;
        }

        context.Output.Note($"Image hook of {stack.Name}, shown once. CI calls, after pushing an image:");
        context.Output.Note($"  curl -fsS -X POST -H \"Authorization: Bearer $FOREST_IMAGE_HOOK\" -H 'Content-Type: application/json' -d '{{\"image\":\"<image>\"}}' {url}");
        context.Output.Note("Token:");
        context.Output.Secret(hook.Token ?? "");
        return ExitCodes.Ok;
    }
}

internal sealed class ImageHookDowngradeCommand() : StackCommand("downgrade", "Let the image hook ask for older tags, or stop it (Editor).")
{
    private readonly Argument<string> state = new("state") { Description = "on or off." };

    protected override void ConfigureStack(Command command)
    {
        state.AcceptOnlyFromAmong("on", "off");
        command.Add(state);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        var on = context.Value(state) == "on";
        await context.Api.SetImageHookDowngradeAsync(stack.Id!.Value, on, context.CancellationToken);
        context.Output.Done($"The image hook of {stack.Name} may {(on ? "" : "not ")}roll back.");
        return ExitCodes.Ok;
    }
}

internal sealed class ImageHookRemoveCommand() : StackCommand("remove", "Remove the stack's image hook (Editor).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, EnvironmentResponse environment, StackResponse stack)
    {
        await context.Api.RemoveImageHookAsync(stack.Id!.Value, context.CancellationToken);
        context.Output.Done($"The image hook of {stack.Name} is removed.");
        return ExitCodes.Ok;
    }
}

internal sealed class ConnectionSetPushTokenCommand() : GitConnectionCommand("set-push-token", "Set the token the aggregator pushes image updates with, typed or from standard input (administrators).")
{
    private readonly Option<string?> tokenUser = new("--token-user") { Description = "User name sent with the token; leave out for the default." };

    protected override void ConfigureConnection(Command command) => command.Add(tokenUser);

    protected override async Task<int> ExecuteAsync(CommandContext context, GitConnectionResponse connection)
    {
        var token = await SecretInput.ReadAsync(context, "Push token");
        await context.Api.SetGitConnectionPushTokenAsync(connection.Id!.Value, context.Value(tokenUser), token, context.CancellationToken);
        context.Output.Done($"Push token of {connection.Name} saved; image policies of its repositories can now commit.");
        return ExitCodes.Ok;
    }
}

internal sealed class ConnectionRemovePushTokenCommand() : GitConnectionCommand("remove-push-token", "Stop image updates of the connection's repositories (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, GitConnectionResponse connection)
    {
        await context.Api.RemoveGitConnectionPushTokenAsync(connection.Id!.Value, context.CancellationToken);
        context.Output.Done($"Push token of {connection.Name} removed.");
        return ExitCodes.Ok;
    }
}

internal sealed class SigningKeyShowCommand() : ForestCommand("show", "Show the aggregator's signing key, to add to git servers (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var keys = await context.Api.ListAggregatorSigningKeysAsync(context.CancellationToken);
        await context.Output.ListAsync(
            keys,
            items =>
            {
                var table = new Table("FINGERPRINT", "CREATED", "STATE", "PUBLIC KEY");
                foreach (var k in items)
                {
                    table.AddRow(k.Fingerprint, CliOutput.Time(k.CreatedAt), k.RetiredAt is null ? "active" : $"retired {CliOutput.Time(k.RetiredAt)}", k.PublicKey);
                }

                return table;
            },
            "The aggregator has no signing key yet; it creates one for the first image update.");
        return ExitCodes.Ok;
    }
}

internal sealed class SigningKeyRotateCommand() : ForestCommand("rotate", "Replace the aggregator's signing key; earlier update commits stay endorsed (administrators).")
{
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void Configure(Command command) => command.Add(yes);

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        Confirmation.Require(context, yes, "replace the aggregator's signing key");
        var key = await context.Api.RotateAggregatorSigningKeyAsync(context.CancellationToken);
        context.Output.Done($"New signing key {key.Fingerprint}; add it to the git servers' service users:");
        context.Output.Secret(key.PublicKey ?? "");
        return ExitCodes.Ok;
    }
}
