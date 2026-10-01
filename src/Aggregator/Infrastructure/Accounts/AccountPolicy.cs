using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Infrastructure.Accounts;

/// <summary>Password and lockout rules of ASP.NET Core Identity.</summary>
public static class AccountPolicy
{
    /// <summary>Length is what makes a password strong; there are no character-class rules (NIST SP 800-63B).</summary>
    public const int MinPasswordLength = 12;

    /// <summary>Failed sign-ins in a row before the account is locked for <see cref="LockoutDuration"/>.</summary>
    public const int MaxFailedSignIns = 5;

    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static void Configure(IdentityOptions options)
    {
        options.Password.RequiredLength = MinPasswordLength;
        options.Password.RequiredUniqueChars = 1;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = MaxFailedSignIns;
        options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;
    }
}
