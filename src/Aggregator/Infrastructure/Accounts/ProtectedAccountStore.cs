using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace ContainerForest.Aggregator.Infrastructure.Accounts;

/// <summary>
/// Identity's user store, except that the authenticator key and the recovery codes are encrypted with Data Protection
/// like every other secret in the database (ADR-0003). Identity keeps both in <c>AspNetUserTokens</c> in plain text;
/// whoever read the database would otherwise have every user's second factor.
/// </summary>
public sealed class ProtectedAccountStore(AggregatorDbContext context, ISecretProtector protector, IdentityErrorDescriber? describer = null)
    : UserOnlyStore<AccountUser, AggregatorDbContext, Guid>(context, describer)
{
    /// <summary>Identity's own provider name for the tokens it keeps for itself (<c>UserStoreBase.InternalLoginProvider</c>).</summary>
    public const string IdentityProvider = "[AspNetUserStore]";

    private static readonly HashSet<string> Protected = ["AuthenticatorKey", "RecoveryCodes"];

    public override async Task<string?> GetTokenAsync(AccountUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        var value = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        return value is null || !IsProtected(loginProvider, name) ? value : protector.Unprotect(SecretPurpose.AuthenticatorSecret, value);
    }

    public override Task SetTokenAsync(AccountUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken) =>
        base.SetTokenAsync(
            user,
            loginProvider,
            name,
            value is null || !IsProtected(loginProvider, name) ? value : protector.Protect(SecretPurpose.AuthenticatorSecret, value),
            cancellationToken);

    private static bool IsProtected(string loginProvider, string name) => loginProvider == IdentityProvider && Protected.Contains(name);
}
