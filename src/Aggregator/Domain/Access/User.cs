using System.Text.RegularExpressions;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Access;

/// <summary>
/// A user as the authorization model sees it. Passwords, 2FA and external logins live in
/// ASP.NET Core Identity in the infrastructure layer, under the same id.
/// </summary>
public sealed partial class User : Entity<UserId>
{
    public const int MinUserNameLength = 3;
    public const int MaxUserNameLength = 64;

    private User(UserId id, string userName, bool isAdmin, DateTimeOffset createdAt)
        : base(id)
    {
        UserName = userName;
        IsAdmin = isAdmin;
        IsActive = true;
        CreatedAt = createdAt;
    }

    // For EF Core.
    private User()
    {
        UserName = null!;
    }

    public string UserName { get; private set; }

    /// <summary>Administrators can do everything, including managing users.</summary>
    public bool IsAdmin { get; private set; }

    /// <summary>A deactivated user keeps their data but has no access at all.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static User Create(string userName, DateTimeOffset now) =>
        new(UserId.New(), NormalizeUserName(userName), isAdmin: false, now);

    /// <summary>The first administrator, created when the system is set up.</summary>
    public static User CreateAdministrator(string userName, DateTimeOffset now) =>
        new(UserId.New(), NormalizeUserName(userName), isAdmin: true, now);

    public void Rename(string userName) => UserName = NormalizeUserName(userName);

    public void GrantAdmin()
    {
        EnsureActive();
        IsAdmin = true;
    }

    public void RevokeAdmin() => IsAdmin = false;

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new DomainConflictException($"User '{UserName}' is deactivated.");
        }
    }

    private static string NormalizeUserName(string userName)
    {
        var trimmed = userName.Trim();
        if (trimmed.Length < MinUserNameLength || trimmed.Length > MaxUserNameLength || !UserNamePattern().IsMatch(trimmed))
        {
            throw new DomainValidationException(
                $"User name must be {MinUserNameLength}-{MaxUserNameLength} characters of letters, digits, '.', '_' or '-'.");
        }

        return trimmed;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex UserNamePattern();
}
