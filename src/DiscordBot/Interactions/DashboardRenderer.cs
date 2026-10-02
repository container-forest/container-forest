using System.Text;
using ContainerForest.DiscordBot.Commands;
using ContainerForest.DiscordBot.Dashboard;
using ContainerForest.DiscordBot.Forest;
using Discord;

namespace ContainerForest.DiscordBot.Interactions;

/// <summary>
/// Draws a <see cref="DashboardScreen"/> as a Components V2 message: one container with text, sections whose
/// accessory is a button, separators and rows of buttons. Every button carries its <see cref="DashboardRoute"/>.
/// </summary>
public static class DashboardRenderer
{
    public static readonly Color Accent = new(0xD47D2C);
    public static readonly Color ErrorAccent = new(0xD83C3E);

    /// <summary>Discord's limit on the text of all text displays in one message.</summary>
    public const int MaxTextLength = 4000;

    /// <summary>Compose output within a notice, so the whole message stays within <see cref="MaxTextLength"/>.</summary>
    private const int MaxNoticeLength = 1800;

    // Disabled buttons still need ids, unique in the message; they are never clicked, so they lead nowhere.
    private const string NoPreviousId = DashboardRoute.Prefix + ":none:previous";
    private const string NoNextId = DashboardRoute.Prefix + ":none:next";

    public static MessageComponent Render(DashboardScreen screen) => screen switch
    {
        EnvironmentsScreen s => Environments(s),
        StacksScreen s => Stacks(s),
        StackScreen s => Stack(s),
        ConfirmScreen s => Confirm(s),
        ErrorScreen s => Error(s),
        _ => throw new ArgumentOutOfRangeException(nameof(screen), screen, null),
    };

    private static MessageComponent Environments(EnvironmentsScreen screen)
    {
        var page = screen.Environments;
        var container = Container("# Environments");
        if (page.Total == 0)
        {
            container.AddComponent(Text("You cannot see any environments yet. Ask an administrator for access."));
        }

        foreach (var environment in page.Items)
        {
            container.AddComponent(Divider());
            var status = environment.Online ? "🟢 Online" : "🔴 Agent offline";
            container.AddComponent(Section(
                $"## {Markdown.Escape(environment.Name)}\n{status}",
                Button("Stacks", "📚", new StacksRoute(Guid.Parse(environment.Id), 0), ButtonStyle.Secondary)));
        }

        container.AddComponent(Divider(large: true));
        container.AddComponent(Pager(page, p => new EnvironmentsRoute(p), new EnvironmentsRoute(page.Number)));
        return Build(container);
    }

    private static MessageComponent Stacks(StacksScreen screen)
    {
        var page = screen.Stacks;
        var environment = screen.Environment;
        var environmentId = Guid.Parse(environment.Id);
        var header = $"# Stacks\nEnvironment: {Markdown.Code(environment.Name)}";
        if (!environment.Online)
        {
            header += "\n🔴 The agent is offline: stacks cannot be changed until it reconnects.";
        }

        var container = Container(header);
        if (page.Total == 0)
        {
            container.AddComponent(Text("No stacks in this environment that you can see."));
        }

        foreach (var row in page.Items)
        {
            container.AddComponent(Divider());
            container.AddComponent(Section(
                StackSummary(row.Stack, row.Containers),
                Button("Details", "➡️", new StackRoute(environmentId, Guid.Parse(row.Stack.Id), page.Number), ButtonStyle.Secondary)));
        }

        container.AddComponent(Divider(large: true));
        container.AddComponent(Pager(page, p => new StacksRoute(environmentId, p), new StacksRoute(environmentId, page.Number)));
        container.AddComponent(new ActionRowBuilder().WithButton(Button("Environments", "⬅️", new EnvironmentsRoute(0))));
        return Build(container);
    }

    private static MessageComponent Stack(StackScreen screen)
    {
        var stack = screen.Stack;
        var text = new StringBuilder($"# {Markdown.Escape(stack.Name)}");
        text.Append($"\nEnvironment: {Markdown.Code(screen.Environment.Name)}");
        if (!screen.Environment.Online)
        {
            text.Append(" · 🔴 agent offline");
        }

        text.Append('\n').Append(Deployed(stack));
        if (stack.IsSystem)
        {
            text.Append("\n🔒 System stack: it is updated through a helper container, not from here.");
        }

        text.Append('\n').Append(ContainersLine(screen.Containers is null ? null : ContainerCount.Of(screen.Containers)));
        foreach (var c in (screen.Containers ?? []).Take(DashboardService.MaxContainerLines))
        {
            text.Append('\n').Append(ReplyFormatter.ContainerLine(c with { CanControl = true }));
        }

        if (screen.Containers is { Count: > DashboardService.MaxContainerLines } all)
        {
            text.Append($"\n…and {all.Count - DashboardService.MaxContainerLines} more.");
        }

        var container = Container(text.ToString());
        if (screen.Notice is { } notice)
        {
            container.AddComponent(Divider());
            container.AddComponent(Text(NoticeText(notice)));
        }

        container.AddComponent(Divider(large: true));
        var operations = screen.Operations;
        if (operations.Count == 0)
        {
            container.AddComponent(Text("-# You can only view this stack."));
        }
        else
        {
            var actions = new ActionRowBuilder();
            foreach (var operation in operations)
            {
                DashboardRoute target = operation.NeedsConfirmation()
                    ? new ConfirmRoute(operation, screen.EnvironmentGuid, screen.StackGuid, screen.StacksPage)
                    : new RunRoute(operation, screen.EnvironmentGuid, screen.StackGuid, screen.StacksPage);
                actions.WithButton(Button(operation.Label(), Emoji(operation), target, Style(operation), disabled: !screen.CanAct));
            }

            container.AddComponent(actions);
        }

        DashboardRoute refresh = screen.DeploymentId is { } deployment
            ? new DeploymentRoute(screen.EnvironmentGuid, screen.StackGuid, deployment, screen.StacksPage)
            : new StackRoute(screen.EnvironmentGuid, screen.StackGuid, screen.StacksPage);
        container.AddComponent(new ActionRowBuilder()
            .WithButton(Button("Back", "⬅️", new StacksRoute(screen.EnvironmentGuid, screen.StacksPage), disabled: screen.Busy))
            .WithButton(Button("Refresh", "🔄", refresh, disabled: screen.Busy)));
        return Build(container);
    }

    private static MessageComponent Confirm(ConfirmScreen screen)
    {
        var operation = screen.Operation;
        var container = Container(
            $"# {operation.Label()} {Markdown.Escape(screen.Stack.Name)}?\n"
            + $"Environment: {Markdown.Code(screen.Environment.Name)}\n\n{operation.Explanation()}");
        container.AddComponent(Divider(large: true));
        container.AddComponent(new ActionRowBuilder()
            .WithButton(Button(
                $"Yes, {operation.Label().ToLowerInvariant()}",
                Emoji(operation),
                new RunRoute(operation, screen.EnvironmentGuid, screen.StackGuid, screen.StacksPage),
                operation == StackOperation.Down ? ButtonStyle.Danger : ButtonStyle.Primary))
            .WithButton(Button("Cancel", "✖️", new StackRoute(screen.EnvironmentGuid, screen.StackGuid, screen.StacksPage))));
        return Build(container);
    }

    private static MessageComponent Error(ErrorScreen screen)
    {
        var container = Container("❌ " + Markdown.Escape(screen.Message)).WithAccentColor(ErrorAccent);
        if (screen.Back is { } back)
        {
            container.AddComponent(new ActionRowBuilder().WithButton(Button("Back", "⬅️", back)));
        }

        return Build(container);
    }

    /// <summary>The lines of a stack in the list: when it was deployed and how many containers run.</summary>
    internal static string StackSummary(ForestStack stack, ContainerCount? containers)
    {
        var name = $"## {Markdown.Escape(stack.Name)}{(stack.IsSystem ? " 🔒" : "")}";
        return $"{name}\n{Deployed(stack)}\n{ContainersLine(containers)}";
    }

    private static string Deployed(ForestStack stack) =>
        stack is { DeployedAt: { } at, DeployedCommit: { } commit }
            ? $"Up: <t:{at.ToUnixTimeSeconds()}:f> (<t:{at.ToUnixTimeSeconds()}:R>) · {Markdown.Code(DashboardService.ShortCommit(commit))}"
            : "Not deployed";

    private static string ContainersLine(ContainerCount? count) => count switch
    {
        null => "Containers: unknown while the agent is unavailable",
        { Total: 0 } => "Containers: none",
        _ => $"Containers: {Markdown.Code($"{count.Running}/{count.Total}")} running",
    };

    internal static string NoticeText(Notice notice)
    {
        var icon = notice.Kind switch
        {
            NoticeKind.Progress => "⏳",
            NoticeKind.Success => "✅",
            NoticeKind.Failure => "❌",
            _ => "ℹ️",
        };
        var header = $"{icon} {Markdown.Escape(notice.Text)}";
        if (notice.Output is not { Count: > 0 } output)
        {
            return header;
        }

        // The end of the output matters most: it says why compose failed.
        var text = ReplyFormatter.WithOutput(header, output);
        return text.Length <= MaxNoticeLength ? text : ReplyFormatter.WithOutput(header, TrimToEnd(output, MaxNoticeLength - header.Length - 40));
    }

    private static List<string> TrimToEnd(IReadOnlyList<string> output, int budget)
    {
        var kept = new List<string>();
        var used = 0;
        for (var i = output.Count - 1; i >= 0 && used + output[i].Length + 1 <= budget; i--)
        {
            kept.Insert(0, output[i]);
            used += output[i].Length + 1;
        }

        return kept;
    }

    private static ActionRowBuilder Pager<T>(PageOf<T> page, Func<int, DashboardRoute> pageRoute, DashboardRoute refresh)
    {
        var previous = page.HasPrevious
            ? Button("Back", "⬅️", pageRoute(page.Number - 1))
            : new ButtonBuilder("Back", NoPreviousId, ButtonStyle.Secondary, emote: new Emoji("⬅️"), isDisabled: true);
        var next = page.HasNext
            ? Button("Next", "➡️", pageRoute(page.Number + 1))
            : new ButtonBuilder("Next", NoNextId, ButtonStyle.Secondary, emote: new Emoji("➡️"), isDisabled: true);
        return new ActionRowBuilder()
            .WithButton(previous)
            .WithButton(next)
            .WithButton(Button($"Refresh · {page.Number + 1}/{page.Count}", "🔄", refresh));
    }

    private static string Emoji(StackOperation operation) => operation switch
    {
        StackOperation.Redeploy => "🔁",
        StackOperation.RedeployWithPull => "⬇️",
        StackOperation.Restart => "🔄",
        StackOperation.Down => "⏹️",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    private static ButtonStyle Style(StackOperation operation) => operation switch
    {
        StackOperation.Redeploy => ButtonStyle.Primary,
        StackOperation.Down => ButtonStyle.Danger,
        _ => ButtonStyle.Secondary,
    };

    private static ButtonBuilder Button(
        string label, string emoji, DashboardRoute route, ButtonStyle style = ButtonStyle.Secondary, bool disabled = false) =>
        new(label, route.ToCustomId(), style, emote: new Emoji(emoji), isDisabled: disabled);

    private static ContainerBuilder Container(string header) =>
        new ContainerBuilder().WithAccentColor(Accent).AddComponent(Text(header));

    private static TextDisplayBuilder Text(string content) => new(content);

    private static SectionBuilder Section(string content, ButtonBuilder accessory) =>
        new SectionBuilder().AddComponent(Text(content)).WithAccessory(accessory);

    private static SeparatorBuilder Divider(bool large = false) =>
        new(isDivider: true, spacing: large ? SeparatorSpacingSize.Large : SeparatorSpacingSize.Small);

    private static MessageComponent Build(ContainerBuilder container) =>
        new ComponentBuilderV2().AddComponent(container).Build();
}
