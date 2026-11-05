using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Domain.Access;

/// <summary>A role given to a user on some scope. Subclasses define which stacks and environments the scope covers.</summary>
public abstract class RoleAssignment : Entity<RoleAssignmentId>
{
    protected RoleAssignment(RoleAssignmentId id, UserId userId, Role role, DateTimeOffset grantedAt)
        : base(id)
    {
        UserId = userId;
        Role = role;
        GrantedAt = grantedAt;
    }

    // For EF Core.
    protected RoleAssignment() { }

    public UserId UserId { get; private init; }

    public Role Role { get; private set; }

    public DateTimeOffset GrantedAt { get; private init; }

    public static RoleAssignment ForEnvironment(UserId userId, EnvironmentId environmentId, Role role, DateTimeOffset now) =>
        new EnvironmentRoleAssignment(RoleAssignmentId.New(), userId, environmentId, EnsureDefined(role), now);

    public static RoleAssignment ForStack(UserId userId, Stack stack, Role role, DateTimeOffset now) =>
        new StackRoleAssignment(RoleAssignmentId.New(), userId, stack.Id, stack.EnvironmentId, EnsureOnStack(EnsureDefined(role)), now);

    public void ChangeRole(Role role) => Role = EnsureAllowed(EnsureDefined(role));

    /// <summary>Rejects a role this kind of scope cannot have.</summary>
    protected virtual Role EnsureAllowed(Role role) => role;

    /// <summary>The environment administrator administers a whole environment; a stack has nothing of the kind.</summary>
    protected static Role EnsureOnStack(Role role) =>
        role == Role.EnvironmentAdmin
            ? throw new DomainValidationException("The environment administrator role is given on a whole environment, not on a stack.")
            : role;

    /// <summary>Whether this role applies to the stack.</summary>
    public abstract bool Covers(Stack stack);

    /// <summary>Whether this role applies to the environment as a whole, including containers outside stacks.</summary>
    public abstract bool CoversWholeEnvironment(EnvironmentId environmentId);

    /// <summary>Whether this assignment makes a private environment visible to its user.</summary>
    public abstract bool Reveals(EnvironmentId environmentId);

    private static Role EnsureDefined(Role role) =>
        Enum.IsDefined(role) ? role : throw new DomainValidationException($"Unknown role {role}.");
}

/// <summary>Role on a whole environment: applies to all its stacks.</summary>
public sealed class EnvironmentRoleAssignment : RoleAssignment
{
    internal EnvironmentRoleAssignment(RoleAssignmentId id, UserId userId, EnvironmentId environmentId, Role role, DateTimeOffset grantedAt)
        : base(id, userId, role, grantedAt) =>
        EnvironmentId = environmentId;

    // For EF Core.
    private EnvironmentRoleAssignment() { }

    public EnvironmentId EnvironmentId { get; private init; }

    public override bool Covers(Stack stack) => stack.EnvironmentId == EnvironmentId;

    public override bool CoversWholeEnvironment(EnvironmentId environmentId) => environmentId == EnvironmentId;

    public override bool Reveals(EnvironmentId environmentId) => environmentId == EnvironmentId;
}

/// <summary>Role on one stack: applies to that stack only, but makes its environment visible.</summary>
public sealed class StackRoleAssignment : RoleAssignment
{
    internal StackRoleAssignment(
        RoleAssignmentId id, UserId userId, StackId stackId, EnvironmentId environmentId, Role role, DateTimeOffset grantedAt)
        : base(id, userId, role, grantedAt)
    {
        StackId = stackId;
        EnvironmentId = environmentId;
    }

    // For EF Core.
    private StackRoleAssignment() { }

    public StackId StackId { get; private init; }

    /// <summary>Environment of the stack, kept to answer <see cref="Reveals"/> without loading the stack.</summary>
    public EnvironmentId EnvironmentId { get; private init; }

    public override bool Covers(Stack stack) => stack.Id == StackId;

    protected override Role EnsureAllowed(Role role) => EnsureOnStack(role);

    public override bool CoversWholeEnvironment(EnvironmentId environmentId) => false;

    public override bool Reveals(EnvironmentId environmentId) => environmentId == EnvironmentId;
}
