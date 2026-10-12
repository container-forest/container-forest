using System.Diagnostics.CodeAnalysis;
using ContainerForest.DiscordBot.Commands;

namespace ContainerForest.DiscordBot.Dashboard;

/// <summary>What the stack screen can do with a stack.</summary>
public enum StackOperation
{
    Redeploy,
    RedeployWithPull,
    Restart,
    Down,
}

/// <summary>
/// One screen of /dashboard, written into a button's custom id. The bot keeps no state between clicks: everything the
/// next screen needs travels in the id, e.g. which page of stacks "Back" returns to.
/// </summary>
/// <remarks>
/// A custom id holds at most 100 characters, so ids are compact: <c>dash:&lt;kind&gt;:&lt;part&gt;:…</c> with
/// GUIDs in 22 characters of base64url. A forged id gains nothing: the aggregator checks every call against the
/// user's permissions.
/// </remarks>
public abstract record DashboardRoute
{
    public const string Prefix = "dash";

    /// <summary>Discord rejects longer custom ids.</summary>
    public const int MaxCustomIdLength = 100;

    /// <summary>Pages larger than this are not real: it bounds the number in an id.</summary>
    public const int MaxPage = 999;

    private const char Separator = ':';

    /// <summary>The short name of the route in the id.</summary>
    protected abstract string Kind { get; }

    public string ToCustomId()
    {
        var id = string.Join(Separator, [Prefix, Kind, .. Parts()]);
        return id.Length <= MaxCustomIdLength
            ? id
            : throw new InvalidOperationException($"Custom id '{id}' is longer than {MaxCustomIdLength} characters.");
    }

    /// <summary>Reads an id written by <see cref="ToCustomId"/>; anything else, e.g. from an older bot, is false.</summary>
    public static bool TryParse(string customId, [NotNullWhen(true)] out DashboardRoute? route)
    {
        route = null;
        var parts = customId.Split(Separator);
        if (parts.Length < 2 || parts[0] != Prefix)
        {
            return false;
        }

        var reader = new PartReader(parts.AsSpan(2).ToArray());
        route = parts[1] switch
        {
            EnvironmentsRoute.Name => reader.Page(out var page) && reader.End ? new EnvironmentsRoute(page) : null,
            StacksRoute.Name => reader.Guid(out var environment) && reader.Page(out var page) && reader.End
                ? new StacksRoute(environment, page)
                : null,
            StackRoute.Name => reader.Guid(out var environment) && reader.Guid(out var stack) && reader.Page(out var page) && reader.End
                ? new StackRoute(environment, stack, page)
                : null,
            ConfirmRoute.Name => reader.Operation(out var operation) && reader.Guid(out var environment) && reader.Guid(out var stack)
                && reader.Page(out var page) && reader.End
                ? new ConfirmRoute(operation, environment, stack, page)
                : null,
            RunRoute.Name => reader.Operation(out var operation) && reader.Guid(out var environment) && reader.Guid(out var stack)
                && reader.Page(out var page) && reader.End
                ? new RunRoute(operation, environment, stack, page)
                : null,
            DeploymentRoute.Name => reader.Guid(out var environment) && reader.Guid(out var stack) && reader.Guid(out var deployment)
                && reader.Page(out var page) && reader.End
                ? new DeploymentRoute(environment, stack, deployment, page)
                : null,
            _ => null,
        };
        return route is not null;
    }

    protected abstract IEnumerable<string> Parts();

    protected static string Encode(Guid value) => CompactGuid.Encode(value);

    protected static string Encode(StackOperation operation) => operation switch
    {
        StackOperation.Redeploy => "rd",
        StackOperation.RedeployWithPull => "rp",
        StackOperation.Restart => "rs",
        StackOperation.Down => "dn",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    /// <summary>Reads the parts after the kind, one at a time, failing on anything malformed.</summary>
    private sealed class PartReader(string[] parts)
    {
        private int next;

        public bool End => next == parts.Length;

        public bool Page(out int page)
        {
            page = 0;
            return Take(out var text) && int.TryParse(text, out page) && page is >= 0 and <= MaxPage && text == page.ToString();
        }

        public bool Guid(out Guid value)
        {
            value = default;
            return Take(out var text) && CompactGuid.TryDecode(text, out value);
        }

        public bool Operation(out StackOperation operation)
        {
            operation = default;
            if (!Take(out var text))
            {
                return false;
            }

            StackOperation? parsed = text switch
            {
                "rd" => StackOperation.Redeploy,
                "rp" => StackOperation.RedeployWithPull,
                "rs" => StackOperation.Restart,
                "dn" => StackOperation.Down,
                _ => null,
            };
            operation = parsed.GetValueOrDefault();
            return parsed is not null;
        }

        private bool Take(out string text)
        {
            text = next < parts.Length ? parts[next++] : "";
            return text.Length > 0;
        }
    }
}

/// <summary>The environments the user can see, one page of them.</summary>
public sealed record EnvironmentsRoute(int Page) : DashboardRoute
{
    internal const string Name = "envs";

    protected override string Kind => Name;

    protected override IEnumerable<string> Parts() => [Page.ToString()];
}

/// <summary>The stacks of one environment, one page of them.</summary>
public sealed record StacksRoute(Guid EnvironmentId, int Page) : DashboardRoute
{
    internal const string Name = "stacks";

    protected override string Kind => Name;

    protected override IEnumerable<string> Parts() => [Encode(EnvironmentId), Page.ToString()];
}

/// <summary>One stack with its containers and actions.</summary>
/// <param name="StacksPage">The page of stacks "Back" returns to.</param>
public sealed record StackRoute(Guid EnvironmentId, Guid StackId, int StacksPage) : DashboardRoute
{
    internal const string Name = "stack";

    protected override string Kind => Name;

    protected override IEnumerable<string> Parts() => [Encode(EnvironmentId), Encode(StackId), StacksPage.ToString()];
}

/// <summary>Asks before an operation that changes or removes the stack's containers.</summary>
public sealed record ConfirmRoute(StackOperation Operation, Guid EnvironmentId, Guid StackId, int StacksPage) : DashboardRoute
{
    internal const string Name = "ask";

    protected override string Kind => Name;

    protected override IEnumerable<string> Parts() =>
        [Encode(Operation), Encode(EnvironmentId), Encode(StackId), StacksPage.ToString()];
}

/// <summary>Runs an operation on the stack.</summary>
public sealed record RunRoute(StackOperation Operation, Guid EnvironmentId, Guid StackId, int StacksPage) : DashboardRoute
{
    internal const string Name = "run";

    protected override string Kind => Name;

    protected override IEnumerable<string> Parts() =>
        [Encode(Operation), Encode(EnvironmentId), Encode(StackId), StacksPage.ToString()];
}

/// <summary>The stack together with how one of its deployments is going.</summary>
public sealed record DeploymentRoute(Guid EnvironmentId, Guid StackId, Guid DeploymentId, int StacksPage) : DashboardRoute
{
    internal const string Name = "dep";

    protected override string Kind => Name;

    protected override IEnumerable<string> Parts() =>
        [Encode(EnvironmentId), Encode(StackId), Encode(DeploymentId), StacksPage.ToString()];
}
