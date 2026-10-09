using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Access;

/// <summary>
/// Who makes a request and how far it may reach: a <see cref="User"/> with the <see cref="AccessScope"/> of the
/// credentials it came with. <see cref="AccessPolicy"/> decides on actors, so a limited API token cannot do more
/// than its limits allow, whatever its user may do.
/// </summary>
public sealed class Actor(User user, AccessScope scope)
{
    public User User { get; } = user;

    public AccessScope Scope { get; } = scope;

    public UserId Id => User.Id;

    public string UserName => User.UserName;

    public bool IsActive => User.IsActive;

    /// <summary>
    /// An active administrator acting without environment limits. An administrator's token limited to some
    /// environments is an Editor there and nothing more.
    /// </summary>
    public bool IsAdmin => User.IsActive && User.IsAdmin && !Scope.IsRestricted;

    /// <summary>The user with all of their rights, as after a sign-in in the panel.</summary>
    public static Actor FromUser(User user) => new(user, AccessScope.Full);

    public static implicit operator Actor(User user) => FromUser(user);
}
