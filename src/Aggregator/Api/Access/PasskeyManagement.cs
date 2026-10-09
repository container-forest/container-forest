using System.Buffers.Text;
using System.Text.Json;
using ContainerForest.Aggregator.Application.Access;
using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Infrastructure.Accounts;
using Microsoft.AspNetCore.Identity;

namespace ContainerForest.Aggregator.Api.Access;

/// <summary>A passkey as the panel shows it; the id is the credential id in base64url.</summary>
internal sealed record PasskeyView(string Id, string Name, DateTimeOffset CreatedAt, bool IsBackedUp, IReadOnlyList<string> Transports);

/// <summary>
/// The passkeys of the signed-in user (only from a session: a token must not add a way to sign in), and of any user
/// for administrators, who can remove a lost one.
/// </summary>
internal sealed class PasskeyManagement(SignInManager<AccountUser> signInManager, CurrentActor currentActor)
{
    public const int MaxNameLength = 64;

    /// <summary>More than anyone needs: phones, laptops, a couple of security keys.</summary>
    public const int MaxPasskeysPerUser = 20;

    private UserManager<AccountUser> Accounts => signInManager.UserManager;

    /// <summary>Options for <c>navigator.credentials.create()</c> for the signed-in user.</summary>
    /// <exception cref="DomainConflictException">The user already has <see cref="MaxPasskeysPerUser"/> passkeys.</exception>
    public async Task<string> MakeCreationOptionsAsync(CancellationToken cancellationToken)
    {
        var (user, account) = await SignedInAsync(cancellationToken);
        if ((await Accounts.GetPasskeysAsync(account)).Count >= MaxPasskeysPerUser)
        {
            throw new DomainConflictException($"A user can have at most {MaxPasskeysPerUser} passkeys; remove one first.");
        }

        return await signInManager.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity
        {
            Id = account.Id.ToString(),
            Name = user.UserName,
            DisplayName = user.UserName,
        });
    }

    /// <summary>Checks the browser's new credential and adds it to the signed-in user.</summary>
    /// <param name="credentialJson">The result of <c>navigator.credentials.create()</c>, serialized with <c>toJSON()</c>.</param>
    /// <exception cref="DomainValidationException">The credential does not check out, or the name is invalid.</exception>
    public async Task<PasskeyView> AddAsync(string credentialJson, string name, CancellationToken cancellationToken)
    {
        var (_, account) = await SignedInAsync(cancellationToken);
        var cleanName = NormalizeName(name);

        PasskeyAttestationResult attestation;
        try
        {
            attestation = await signInManager.PerformPasskeyAttestationAsync(credentialJson);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or ArgumentException)
        {
            throw new DomainValidationException("The passkey could not be checked; start adding it again.");
        }

        // The options were made for this user; a credential for anyone else is not theirs to add.
        if (!attestation.Succeeded || attestation.UserEntity.Id != account.Id.ToString())
        {
            throw new DomainValidationException("The passkey could not be checked; start adding it again.");
        }

        var passkey = attestation.Passkey;
        passkey.Name = cleanName;
        Ensure(await Accounts.AddOrUpdatePasskeyAsync(account, passkey));
        return View(passkey);
    }

    public async Task<IReadOnlyList<PasskeyView>> ListMineAsync(CancellationToken cancellationToken)
    {
        var (_, account) = await SignedInAsync(cancellationToken);
        return await ListAsync(account);
    }

    /// <exception cref="NotFoundException">The user has no such passkey.</exception>
    public async Task<PasskeyView> RenameAsync(string id, string name, CancellationToken cancellationToken)
    {
        var (_, account) = await SignedInAsync(cancellationToken);
        var passkey = await Accounts.GetPasskeyAsync(account, CredentialId(id)) ?? throw new NotFoundException("Passkey", id);
        passkey.Name = NormalizeName(name);
        Ensure(await Accounts.AddOrUpdatePasskeyAsync(account, passkey));
        return View(passkey);
    }

    /// <exception cref="NotFoundException">The user has no such passkey.</exception>
    public async Task RemoveMineAsync(string id, CancellationToken cancellationToken)
    {
        var (_, account) = await SignedInAsync(cancellationToken);
        await RemoveAsync(account, id);
    }

    /// <exception cref="NotFoundException">The user does not exist or has no account.</exception>
    public async Task<IReadOnlyList<PasskeyView>> ListForUserAsync(UserId userId, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        return await ListAsync(await AccountOfAsync(userId));
    }

    /// <exception cref="NotFoundException">The user, or their passkey, does not exist.</exception>
    public async Task RemoveForUserAsync(UserId userId, string id, CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        await RemoveAsync(await AccountOfAsync(userId), id);
    }

    private async Task RemoveAsync(AccountUser account, string id)
    {
        var credentialId = CredentialId(id);
        if (await Accounts.GetPasskeyAsync(account, credentialId) is null)
        {
            throw new NotFoundException("Passkey", id);
        }

        Ensure(await Accounts.RemovePasskeyAsync(account, credentialId));
    }

    private async Task<IReadOnlyList<PasskeyView>> ListAsync(AccountUser account) =>
        (await Accounts.GetPasskeysAsync(account)).OrderBy(p => p.CreatedAt).Select(View).ToList();

    private async Task<(User User, AccountUser Account)> SignedInAsync(CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        return (user, await AccountOfAsync(user.Id));
    }

    // Users from the removed dev endpoint have no account, so no passkeys either.
    private async Task<AccountUser> AccountOfAsync(UserId userId) =>
        await Accounts.FindByIdAsync(userId.Value.ToString()) ?? throw new NotFoundException("Account", userId);

    private static PasskeyView View(UserPasskeyInfo passkey) => new(
        Base64Url.EncodeToString(passkey.CredentialId),
        passkey.Name ?? "Passkey",
        passkey.CreatedAt,
        passkey.IsBackedUp,
        passkey.Transports ?? []);

    private static byte[] CredentialId(string id)
    {
        try
        {
            return Base64Url.DecodeFromChars(id);
        }
        catch (FormatException)
        {
            throw new NotFoundException("Passkey", id);
        }
    }

    private static string NormalizeName(string name)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length is > 0 and <= MaxNameLength
            ? cleaned
            : throw new DomainValidationException($"Passkey name must be 1-{MaxNameLength} characters.");
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Identity refused a passkey change: {string.Join("; ", result.Errors.Select(e => e.Code))}");
        }
    }
}
