namespace ContainerForest.Aggregator.Domain.Audit;

/// <summary>What was done. Stored by name, so new actions can be added anywhere in the list.</summary>
public enum AuditAction
{
    // Account: the user's own sign-in and credentials.
    SignedIn,
    SignInFailed,
    SignedOut,
    PasswordChanged,
    DiscordLinked,
    DiscordUnlinked,
    TwoFactorEnabled,
    TwoFactorDisabled,
    RecoveryCodesGenerated,

    // Users: an administrator's changes to other users.
    UserCreated,
    UserRenamed,
    UserDeactivated,
    UserActivated,
    AdminGranted,
    AdminRevoked,
    PasswordReset,
    UserUnlocked,
    TwoFactorReset,
    RoleGranted,
    RoleChanged,
    RoleRevoked,

    // Tokens.
    ApiTokenCreated,
    ApiTokenRevoked,
    SigningKeyAdded,
    SigningKeyChanged,
    SigningKeyRemoved,

    // Environments.
    EnvironmentCreated,
    EnvironmentUpdated,
    ComposePolicyChanged,
    EnvironmentRemoved,
    EnrollmentTokenIssued,
    AgentRevoked,

    // Stacks.
    StackCreated,
    StackRemoved,
    StackSourceChanged,
    DeployHookCreated,
    DeployHookRemoved,
    StackRestarted,
    StackTakenDown,
    StackEndorsementChanged,
    StackPolicyAllowancesChanged,
    StackPolicyUnrestrictedChanged,
    StackHoldReleased,
    ImagePolicyAdded,
    ImagePolicyChanged,
    ImagePolicyRemoved,
    ImagePinned,
    ImageUnpinned,
    ImageHookCreated,
    ImageHookRemoved,

    // Unmanaged stacks (ADR-0016): compose projects run by hand on a host, without a stack record.
    UnmanagedStackStarted,
    UnmanagedStackPulled,
    UnmanagedStackRestarted,
    UnmanagedStackTakenDown,

    // Deployments.
    DeploymentQueued,

    // Containers.
    ContainerStarted,
    ContainerStopped,
    ContainerRestarted,
    ContainerRemoved,

    // Secrets.
    SecretsDecrypted,

    // System: settings of the whole system and what depends on them.
    IdentityConfirmed,
    SettingsChanged,
    BackupRequested,
    GitPushCredentialChanged,
    AggregatorSigningKeyRotated,
}

/// <summary>Groups of actions, for filtering the log.</summary>
public enum AuditCategory
{
    Account,
    Users,
    Tokens,
    Environments,
    Stacks,
    Deployments,
    Containers,
    Secrets,
    System,
}

/// <summary>How the attempt ended.</summary>
public enum AuditOutcome
{
    Succeeded,

    /// <summary>The actor lacked the permission; nothing changed.</summary>
    Denied,

    /// <summary>Allowed, but did not go through: invalid input, a conflict, an offline agent, a failed compose run.</summary>
    Failed,
}

public static class AuditActions
{
    public static AuditCategory CategoryOf(AuditAction action) => action switch
    {
        AuditAction.SignedIn or AuditAction.SignInFailed or AuditAction.SignedOut or AuditAction.PasswordChanged
            or AuditAction.DiscordLinked or AuditAction.DiscordUnlinked or AuditAction.TwoFactorEnabled
            or AuditAction.TwoFactorDisabled or AuditAction.RecoveryCodesGenerated => AuditCategory.Account,
        AuditAction.UserCreated or AuditAction.UserRenamed or AuditAction.UserDeactivated or AuditAction.UserActivated
            or AuditAction.AdminGranted or AuditAction.AdminRevoked or AuditAction.PasswordReset or AuditAction.UserUnlocked
            or AuditAction.TwoFactorReset
            or AuditAction.RoleGranted or AuditAction.RoleChanged or AuditAction.RoleRevoked => AuditCategory.Users,
        AuditAction.ApiTokenCreated or AuditAction.ApiTokenRevoked or AuditAction.SigningKeyAdded
            or AuditAction.SigningKeyChanged or AuditAction.SigningKeyRemoved => AuditCategory.Tokens,
        AuditAction.EnvironmentCreated or AuditAction.EnvironmentUpdated or AuditAction.EnvironmentRemoved or AuditAction.ComposePolicyChanged
            or AuditAction.EnrollmentTokenIssued or AuditAction.AgentRevoked => AuditCategory.Environments,
        AuditAction.StackCreated or AuditAction.StackRemoved or AuditAction.StackSourceChanged or AuditAction.DeployHookCreated
            or AuditAction.DeployHookRemoved or AuditAction.StackRestarted or AuditAction.StackTakenDown
            or AuditAction.StackEndorsementChanged or AuditAction.StackPolicyAllowancesChanged
            or AuditAction.StackPolicyUnrestrictedChanged or AuditAction.StackHoldReleased or AuditAction.ImagePolicyAdded or AuditAction.ImagePolicyChanged
            or AuditAction.ImagePolicyRemoved or AuditAction.ImagePinned or AuditAction.ImageUnpinned
            or AuditAction.ImageHookCreated or AuditAction.ImageHookRemoved or AuditAction.UnmanagedStackStarted
            or AuditAction.UnmanagedStackPulled or AuditAction.UnmanagedStackRestarted
            or AuditAction.UnmanagedStackTakenDown => AuditCategory.Stacks,
        AuditAction.DeploymentQueued => AuditCategory.Deployments,
        AuditAction.ContainerStarted or AuditAction.ContainerStopped or AuditAction.ContainerRestarted
            or AuditAction.ContainerRemoved => AuditCategory.Containers,
        AuditAction.SecretsDecrypted => AuditCategory.Secrets,
        AuditAction.IdentityConfirmed or AuditAction.SettingsChanged or AuditAction.BackupRequested
            or AuditAction.GitPushCredentialChanged or AuditAction.AggregatorSigningKeyRotated => AuditCategory.System,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown audit action."),
    };
}
