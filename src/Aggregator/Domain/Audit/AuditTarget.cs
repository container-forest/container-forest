using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Audit;

public enum AuditTargetKind
{
    User,
    RoleAssignment,
    ApiToken,
    SigningKey,
    Environment,
    Stack,
    Container,
    File,
    Settings,
}

/// <summary>
/// What an action was done to. Each kind knows its id, its name at the time and where it lives (environment, stack),
/// so the log can be filtered by environment and still reads correctly after the object is renamed or removed.
/// </summary>
public abstract record AuditTarget
{
    public abstract AuditTargetKind Kind { get; }

    /// <summary>The object's id as text; null when it has none yet, e.g. a user that failed to be created.</summary>
    public abstract string? Id { get; }

    /// <summary>A name a person recognises: a user name, a stack name, a file path.</summary>
    public abstract string Name { get; }

    public virtual EnvironmentId? EnvironmentId => null;

    public virtual StackId? StackId => null;
}

/// <param name="UserId">Null when the user does not exist, e.g. a failed sign-in with an unknown name.</param>
public sealed record UserAuditTarget(UserId? UserId, string UserName) : AuditTarget
{
    public override AuditTargetKind Kind => AuditTargetKind.User;

    public override string? Id => UserId?.ToString();

    public override string Name => UserName;
}

/// <summary>A user's role on an environment or, with <paramref name="OnStack"/>, on one of its stacks.</summary>
public sealed record RoleAssignmentAuditTarget(
    RoleAssignmentId? AssignmentId, string UserName, EnvironmentId OnEnvironment, StackId? OnStack) : AuditTarget
{
    public override AuditTargetKind Kind => AuditTargetKind.RoleAssignment;

    public override string? Id => AssignmentId?.ToString();

    public override string Name => UserName;

    public override EnvironmentId? EnvironmentId => OnEnvironment;

    public override StackId? StackId => OnStack;
}

public sealed record ApiTokenAuditTarget(ApiTokenId? TokenId, string TokenName) : AuditTarget
{
    public override AuditTargetKind Kind => AuditTargetKind.ApiToken;

    public override string? Id => TokenId?.ToString();

    public override string Name => TokenName;
}

/// <param name="KeyName">The key's name and, after it, its fingerprint.</param>
public sealed record SigningKeyAuditTarget(SigningKeyId? KeyId, string KeyName) : AuditTarget
{
    public override AuditTargetKind Kind => AuditTargetKind.SigningKey;

    public override string? Id => KeyId?.ToString();

    public override string Name => KeyName;
}

public sealed record EnvironmentAuditTarget(EnvironmentId? Environment, string EnvironmentName) : AuditTarget
{
    public override AuditTargetKind Kind => AuditTargetKind.Environment;

    public override string? Id => Environment?.ToString();

    public override string Name => EnvironmentName;

    public override EnvironmentId? EnvironmentId => Environment;
}

/// <param name="Stack">Null for a stack that failed to be created.</param>
public sealed record StackAuditTarget(StackId? Stack, string StackName, EnvironmentId InEnvironment) : AuditTarget
{
    public override AuditTargetKind Kind => AuditTargetKind.Stack;

    public override string? Id => Stack?.ToString();

    public override string Name => StackName;

    public override EnvironmentId? EnvironmentId => InEnvironment;

    public override StackId? StackId => Stack;
}

/// <summary>A container by its name: compose recreates containers with new ids, the name stays.</summary>
public sealed record ContainerAuditTarget(string ContainerName, EnvironmentId InEnvironment, StackId? InStack) : AuditTarget
{
    public override AuditTargetKind Kind => AuditTargetKind.Container;

    public override string? Id => ContainerName;

    public override string Name => ContainerName;

    public override EnvironmentId? EnvironmentId => InEnvironment;

    public override StackId? StackId => InStack;
}

/// <summary>A file in a repository, by its path; never its content.</summary>
public sealed record FileAuditTarget(string Path) : AuditTarget
{
    public override AuditTargetKind Kind => AuditTargetKind.File;

    public override string? Id => null;

    public override string Name => Path;
}

/// <summary>A section of the system's settings, by its name; never its values.</summary>
public sealed record SettingsAuditTarget(string Section) : AuditTarget
{
    public override AuditTargetKind Kind => AuditTargetKind.Settings;

    public override string? Id => Section;

    public override string Name => Section;
}
