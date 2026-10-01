using ContainerForest.Aggregator.Domain.Access;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Infrastructure.Accounts;

/// <summary>
/// The sign-in side of a <see cref="User"/> in ASP.NET Core Identity: password hash, lockout, security stamp.
/// It has the id of the domain user; everything about permissions stays in <see cref="User"/>.
/// </summary>
public sealed class AccountUser : IdentityUser<Guid>
{
    public AccountUser(User user)
    {
        Id = user.Id.Value;
        UserName = user.UserName;
    }

    // For EF Core.
    private AccountUser()
    {
    }
}
