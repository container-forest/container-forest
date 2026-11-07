using System.Text.RegularExpressions;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Policies;

/// <summary>
/// An exception to the compose policy (ADR-0012): "privileged", "capability:SYS_ADMIN", "path-rw:/srv/media" and so on.
/// Every violation names the allowance that would lift it, so the same string is what an administrator types. Given
/// for a whole environment (its allowlist) or for one stack.
/// </summary>
public sealed partial record PolicyAllowance
{
    public const string Privileged = "privileged";
    public const string Host = "host";
    public const string DockerSocket = "docker-socket";
    public const string Capability = "capability";
    public const string SecurityOpt = "security-opt";
    public const string Device = "device";
    public const string Gpus = "gpus";
    public const string DeviceCgroupRules = "device-cgroup-rules";
    public const string CgroupParent = "cgroup-parent";
    public const string Runtime = "runtime";
    public const string Provider = "provider";
    public const string Path = "path";
    public const string PathReadWrite = "path-rw";
    public const string Port = "port";
    public const string Network = "network";
    public const string NetworkDriver = "network-driver";
    public const string Volume = "volume";
    public const string VolumeDriver = "volume-driver";
    public const string ForeignContainer = "foreign-container";
    public const string Propagation = "propagation";
    public const string Label = "label";
    public const string ContainerName = "container-name";
    public const string Key = "key";

    public const int MaxLength = 512;

    /// <summary>Namespaces a container can share with the host.</summary>
    public static readonly IReadOnlySet<string> HostNamespaces = new HashSet<string>(StringComparer.Ordinal)
    {
        "network", "pid", "ipc", "uts", "userns", "cgroup",
    };

    private static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.Ordinal)
    {
        Privileged, DockerSocket, Gpus, DeviceCgroupRules, CgroupParent, Provider, VolumeDriver, ForeignContainer, Propagation, ContainerName,
    };

    private PolicyAllowance(string kind, string? argument)
    {
        Kind = kind;
        Argument = argument;
    }

    /// <summary>What it allows, e.g. "capability".</summary>
    public string Kind { get; }

    /// <summary>Which one, e.g. "SYS_ADMIN"; null for a flag such as "privileged".</summary>
    public string? Argument { get; }

    public string Value => Argument is null ? Kind : $"{Kind}:{Argument}";

    public override string ToString() => Value;

    public static PolicyAllowance Flag(string kind) =>
        Flags.Contains(kind) ? new PolicyAllowance(kind, null) : throw new ArgumentException($"'{kind}' is not a flag.", nameof(kind));

    public static PolicyAllowance Of(string kind, string argument) => Parse($"{kind}:{argument}");

    /// <exception cref="DomainValidationException">Not an allowance this policy knows.</exception>
    public static PolicyAllowance Parse(string text)
    {
        text = text.Trim();
        if (text.Length is 0 or > MaxLength || text.Any(char.IsControl))
        {
            throw Invalid(text, $"an allowance is 1–{MaxLength} printable characters");
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var kind = colon < 0 ? text : text[..colon];
        var argument = colon < 0 ? null : text[(colon + 1)..];
        if (argument is null)
        {
            return Flags.Contains(kind) ? new PolicyAllowance(kind, null) : throw Invalid(text, "unknown allowance or missing ':' and value");
        }

        if (Flags.Contains(kind))
        {
            throw Invalid(text, $"'{kind}' takes no value");
        }

        var normalized = kind switch
        {
            Host => HostNamespaces.Contains(argument) ? argument : throw Invalid(text, "the namespace is one of network, pid, ipc, uts, userns, cgroup"),
            Capability => CapabilityName(argument) ?? throw Invalid(text, "a capability is a name such as SYS_ADMIN"),
            SecurityOpt => argument.Length > 0 && !argument.Any(char.IsWhiteSpace) ? argument : throw Invalid(text, "a security option without spaces"),
            Device or Path or PathReadWrite => HostPath.TryNormalize(argument) ?? throw Invalid(text, "an absolute path without '.' or '..' segments"),
            Port => int.TryParse(argument, out var port) && port is > 0 and < 1024 && argument == port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ? argument
                : throw Invalid(text, "ports below 1024 need an allowance; give the number"),
            Runtime or Network or NetworkDriver or Volume => NamePattern().IsMatch(argument) ? argument : throw Invalid(text, "a name of letters, digits, '.', '_' and '-'"),
            Label => LabelPattern().IsMatch(argument) ? argument : throw Invalid(text, "a label key"),
            Key => KeyPattern().IsMatch(argument) ? argument : throw Invalid(text, "a compose key such as storage_opt"),
            _ => throw Invalid(text, "unknown allowance"),
        };
        return new PolicyAllowance(kind, normalized);
    }

    /// <summary>"cap_sys_admin" and "SYS_ADMIN" are the same capability; null when it is not a capability name.</summary>
    public static string? CapabilityName(string name)
    {
        var upper = name.Trim().ToUpperInvariant();
        if (upper.StartsWith("CAP_", StringComparison.Ordinal))
        {
            upper = upper[4..];
        }

        return CapabilityPattern().IsMatch(upper) ? upper : null;
    }

    /// <summary>
    /// Whether this allowance lifts a violation that needs <paramref name="required"/>. Paths cover what lies under them,
    /// and write access covers reading; anything else must be the same allowance.
    /// </summary>
    public bool Covers(PolicyAllowance required)
    {
        if (required.Kind is Path or PathReadWrite && Kind is Path or PathReadWrite)
        {
            var writeOk = Kind == PathReadWrite || required.Kind == Path;
            return writeOk && HostPath.IsAtOrUnder(required.Argument!, Argument!);
        }

        return this == required;
    }

    /// <summary>Whether this allowance names exactly the path required: protected paths open only that way.</summary>
    public bool CoversExactly(PolicyAllowance required) =>
        required.Kind is Path or PathReadWrite && Kind is Path or PathReadWrite
            ? (Kind == PathReadWrite || required.Kind == Path) && Argument == required.Argument
            : this == required;

    private static DomainValidationException Invalid(string text, string why) =>
        new($"'{text}' is not a compose policy allowance: {why}.");

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,31}\z")]
    private static partial Regex CapabilityPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,254}\z")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_./-]{0,254}\z")]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,63}\z")]
    private static partial Regex KeyPattern();
}
