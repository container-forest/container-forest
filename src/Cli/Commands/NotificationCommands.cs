using System.CommandLine;
using ContainerForest.Cli.Api;
using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Commands;

/// <summary>A command about one notification channel (administrators), named as its argument.</summary>
internal abstract class ChannelCommand(string name, string description) : ForestCommand(name, description)
{
    private readonly Argument<string> channelArgument = new("channel") { Description = "Channel name or id." };

    protected override void Configure(Command command)
    {
        command.Add(channelArgument);
        ConfigureChannel(command);
    }

    protected sealed override async Task<int> ExecuteAsync(CommandContext context) =>
        await ExecuteAsync(context, await context.Api.FindNotificationChannelAsync(context.Value(channelArgument), context.CancellationToken));

    protected virtual void ConfigureChannel(Command command)
    {
    }

    protected abstract Task<int> ExecuteAsync(CommandContext context, NotificationChannelResponse channel);

    /// <summary>The events of a rule: <c>--event</c> repeated, or <c>all</c>.</summary>
    internal static Option<string[]> EventsOption(bool required) => new("--event")
    {
        Description = $"Event to notify about; repeat for more, or 'all'. One of: {string.Join(", ", ForestApi.NotificationEvents)}.",
        Required = required,
    };

    internal static IReadOnlyList<string> Events(string[] given)
    {
        if (given.Any(e => string.Equals(e, "all", StringComparison.OrdinalIgnoreCase)))
        {
            return ForestApi.NotificationEvents;
        }

        return given
            .Select(e => ForestApi.NotificationEvents.FirstOrDefault(known => string.Equals(known, e, StringComparison.OrdinalIgnoreCase))
                ?? throw new CliException($"Unknown event '{e}'; use one of {string.Join(", ", ForestApi.NotificationEvents)} or all.", ExitCodes.Usage))
            .Distinct()
            .ToList();
    }

    /// <summary>On or off from <c>--enable</c> / <c>--disable</c>; <paramref name="current"/> without either.</summary>
    internal static bool Enabled(CommandContext context, Option<bool> enable, Option<bool> disable, bool current) =>
        (context.Value(enable), context.Value(disable)) switch
        {
            (true, true) => throw new CliException("Choose --enable or --disable, not both.", ExitCodes.Usage),
            (true, _) => true,
            (_, true) => false,
            _ => current,
        };
}

internal sealed class ChannelListCommand() : ForestCommand("list", "List notification channels (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var channels = await context.Api.ListNotificationChannelsAsync(context.CancellationToken);
        await context.Output.ListAsync(
            channels,
            items =>
            {
                var table = new Table("NAME", "DISCORD CHANNEL", "ENABLED", "ID");
                foreach (var c in items)
                {
                    table.AddRow(c.Name, c.DiscordChannelId, CliOutput.YesNo(c.IsEnabled), c.Id?.ToString());
                }

                return table;
            },
            "No notification channels.");
        return ExitCodes.Ok;
    }
}

internal sealed class ChannelAddCommand() : ForestCommand("add", "Add a Discord channel the bot posts notifications to (administrators).")
{
    private readonly Argument<string> name = new("name") { Description = "Channel name in container-forest." };
    private readonly Option<string> discordChannel = new("--discord-channel") { Description = "Discord channel id (Developer Mode → Copy Channel ID).", Required = true };

    protected override void Configure(Command command)
    {
        command.Add(name);
        command.Add(discordChannel);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var channel = await context.Api.AddNotificationChannelAsync(context.Value(name), context.Value(discordChannel)!, context.CancellationToken);
        context.Output.Done($"Channel {channel.Name} added; add rules with 'forest notification rule add {channel.Name} --event …'.");
        return ExitCodes.Ok;
    }
}

internal sealed class ChannelUpdateCommand() : ChannelCommand("update", "Rename, move or switch a channel on or off (administrators).")
{
    private readonly Option<string?> name = new("--name") { Description = "New name." };
    private readonly Option<string?> discordChannel = new("--discord-channel") { Description = "New Discord channel id." };
    private readonly Option<bool> enable = new("--enable") { Description = "Deliver notifications to it." };
    private readonly Option<bool> disable = new("--disable") { Description = "Stop delivering to it." };

    protected override void ConfigureChannel(Command command)
    {
        command.Add(name);
        command.Add(discordChannel);
        command.Add(enable);
        command.Add(disable);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, NotificationChannelResponse channel)
    {
        var updated = await context.Api.UpdateNotificationChannelAsync(
            channel.Id!.Value,
            context.Value(name) ?? channel.Name!,
            context.Value(discordChannel) ?? channel.DiscordChannelId!,
            Enabled(context, enable, disable, channel.IsEnabled == true),
            context.CancellationToken);
        context.Output.Done($"Channel {updated.Name} updated ({(updated.IsEnabled == true ? "enabled" : "disabled")}).");
        return ExitCodes.Ok;
    }
}

internal sealed class ChannelRemoveCommand() : ChannelCommand("remove", "Remove a channel and its rules (administrators).")
{
    private readonly Option<bool> yes = Confirmation.Option();

    protected override void ConfigureChannel(Command command) => command.Add(yes);

    protected override async Task<int> ExecuteAsync(CommandContext context, NotificationChannelResponse channel)
    {
        Confirmation.Require(context, yes, $"remove channel {channel.Name} with its rules");
        await context.Api.RemoveNotificationChannelAsync(channel.Id!.Value, context.CancellationToken);
        context.Output.Done($"Channel {channel.Name} removed.");
        return ExitCodes.Ok;
    }
}

internal sealed class RuleListCommand() : ChannelCommand("list", "List a channel's rules (administrators).")
{
    protected override async Task<int> ExecuteAsync(CommandContext context, NotificationChannelResponse channel)
    {
        var rules = await context.Api.ListNotificationRulesAsync(channel.Id!.Value, context.CancellationToken);
        await context.Output.ListAsync(
            rules,
            items =>
            {
                var table = new Table("SCOPE", "EVENTS", "ENABLED", "ID");
                foreach (var r in items)
                {
                    var scope = r.StackName is { } stack ? $"{r.EnvironmentName}/{stack}" : r.EnvironmentName ?? "(everything)";
                    table.AddRow(scope, string.Join(",", r.Events ?? []), CliOutput.YesNo(r.IsEnabled), r.Id?.ToString());
                }

                return table;
            },
            $"{channel.Name} has no rules.");
        return ExitCodes.Ok;
    }
}

internal sealed class RuleAddCommand() : ChannelCommand("add", "Add a rule: which events from where go to the channel (administrators).")
{
    private readonly Option<string[]> events = EventsOption(required: true);
    private readonly Option<string?> environment = new("--env", "-e") { Description = "Only this environment; leave out for the whole system." };
    private readonly Option<string?> stack = new("--stack") { Description = "Only this stack of the environment." };

    protected override void ConfigureChannel(Command command)
    {
        command.Add(events);
        command.Add(environment);
        command.Add(stack);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, NotificationChannelResponse channel)
    {
        Guid? environmentId = null;
        Guid? stackId = null;
        if (context.Value(environment) is { } envName)
        {
            var env = await context.Api.ResolveEnvironmentAsync(envName, null, context.CancellationToken);
            environmentId = env.Id;
            stackId = context.Value(stack) is { } stackName ? (await context.Api.FindStackAsync(env, stackName, context.CancellationToken)).Id : null;
        }
        else if (context.Value(stack) is not null)
        {
            throw new CliException("--stack needs --env.", ExitCodes.Usage);
        }

        var rule = await context.Api.AddNotificationRuleAsync(
            channel.Id!.Value, Events(context.Value(events) ?? []), environmentId, stackId, context.CancellationToken);
        context.Output.Done($"Rule {rule.Id} added to {channel.Name}.");
        return ExitCodes.Ok;
    }
}

internal sealed class RuleUpdateCommand() : ChannelCommand("update", "Change a rule's events or switch it on or off; its scope stays (administrators).")
{
    private readonly Argument<Guid> rule = new("rule") { Description = "Rule id, see 'forest notification rule list'." };
    private readonly Option<string[]> events = EventsOption(required: false);
    private readonly Option<bool> enable = new("--enable") { Description = "Switch the rule on." };
    private readonly Option<bool> disable = new("--disable") { Description = "Switch the rule off." };

    protected override void ConfigureChannel(Command command)
    {
        command.Add(rule);
        command.Add(events);
        command.Add(enable);
        command.Add(disable);
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, NotificationChannelResponse channel)
    {
        var rules = await context.Api.ListNotificationRulesAsync(channel.Id!.Value, context.CancellationToken);
        var current = rules.FirstOrDefault(r => r.Id == context.Value(rule))
            ?? throw new CliException($"{channel.Name} has no rule {context.Value(rule)}.", ExitCodes.Denied);
        var given = context.Value(events) ?? [];
        var updated = await context.Api.UpdateNotificationRuleAsync(
            current.Id!.Value,
            given.Length > 0 ? Events(given) : current.Events ?? [],
            Enabled(context, enable, disable, current.IsEnabled == true),
            context.CancellationToken);
        context.Output.Done($"Rule {updated.Id} updated.");
        return ExitCodes.Ok;
    }
}

internal sealed class RuleRemoveCommand() : ForestCommand("remove", "Remove a rule (administrators).")
{
    private readonly Argument<Guid> rule = new("rule") { Description = "Rule id." };

    protected override void Configure(Command command) => command.Add(rule);

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        await context.Api.RemoveNotificationRuleAsync(context.Value(rule), context.CancellationToken);
        context.Output.Done("Rule removed.");
        return ExitCodes.Ok;
    }
}

internal sealed class NotificationHistoryCommand() : ForestCommand("history", "Show sent and pending notifications, newest first (administrators).")
{
    private readonly Option<int> limit = new("--limit", "-n") { Description = "How many.", DefaultValueFactory = _ => 50 };

    protected override void Configure(Command command) => command.Add(limit);

    protected override async Task<int> ExecuteAsync(CommandContext context)
    {
        var notifications = await context.Api.ListNotificationsAsync(context.Value(limit), context.CancellationToken);
        await context.Output.ListAsync(
            notifications,
            items =>
            {
                var table = new Table("WHEN", "STATUS", "CHANNEL", "EVENT", "SUMMARY");
                foreach (var n in items)
                {
                    table.AddRow(CliOutput.Time(n.OccurredAt), n.Status?.ToString().ToLowerInvariant(), n.ChannelName, n.Kind, n.Error is null ? n.Summary : $"{n.Summary} ({n.Error})");
                }

                return table;
            },
            "No notifications yet.");
        return ExitCodes.Ok;
    }
}
