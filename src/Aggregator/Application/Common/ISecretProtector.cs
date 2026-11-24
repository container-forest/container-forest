namespace ContainerForest.Aggregator.Application.Common;

/// <summary>What a protected value is used for. Each purpose has its own key, so one kind cannot be decrypted as another.</summary>
public enum SecretPurpose
{
    RegistryPassword,
    GitAccessToken,
    AgeIdentity,
    WebhookSecret,
    StoredSetting,
    IdentityConfirmation,
    GitPushToken,
    AggregatorSigningKey,

    /// <summary>The shared key of a user's authenticator app (TOTP), and their unused recovery codes.</summary>
    AuthenticatorSecret,

    /// <summary>The state between a correct password and the two-factor code, in the <c>cf_2fa</c> cookie.</summary>
    TwoFactorSignIn,
}

/// <summary>Encrypts secrets the aggregator keeps in its database, such as registry passwords.</summary>
public interface ISecretProtector
{
    string Protect(SecretPurpose purpose, string plaintext);

    /// <exception cref="SecretUnreadableException">The value was protected with a key the aggregator no longer has, or was changed.</exception>
    string Unprotect(SecretPurpose purpose, string protectedValue);
}

/// <summary>A stored secret cannot be decrypted, e.g. after the key ring was lost; it has to be set again.</summary>
public sealed class SecretUnreadableException(SecretPurpose purpose, Exception innerException)
    : Exception($"A stored {purpose} cannot be decrypted.", innerException)
{
    public SecretPurpose Purpose { get; } = purpose;
}
