using System.Security.Cryptography;
using ContainerForest.Aggregator.Application.Common;
using Microsoft.AspNetCore.DataProtection;

namespace ContainerForest.Aggregator.Infrastructure.Security;

/// <summary>Configuration section "DataProtection" (env: CF_DATAPROTECTION__...).</summary>
public sealed class SecretProtectionOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>
    /// Where the key ring is kept; a relative path is resolved against the working directory. Losing it makes every
    /// protected secret unreadable, so it is backed up with the database and, like the database, readable only by the aggregator.
    /// </summary>
    public string KeyDirectory { get; set; } = "data-protection-keys";
}

/// <summary>Adapter from <see cref="ISecretProtector"/> to ASP.NET Core Data Protection, which also rotates the keys.</summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public string Protect(SecretPurpose purpose, string plaintext) => Protector(purpose).Protect(plaintext);

    public string Unprotect(SecretPurpose purpose, string protectedValue)
    {
        try
        {
            return Protector(purpose).Unprotect(protectedValue);
        }
        catch (CryptographicException ex)
        {
            throw new SecretUnreadableException(purpose, ex);
        }
    }

    // A separate purpose string per kind of secret: a registry password cannot be unprotected as a git token.
    private IDataProtector Protector(SecretPurpose purpose) => provider.CreateProtector($"ContainerForest.Secrets.{purpose}");
}
