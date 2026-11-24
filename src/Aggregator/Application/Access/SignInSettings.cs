using ContainerForest.Aggregator.Application.Audit;
using ContainerForest.Aggregator.Application.Notifications;
using ContainerForest.Aggregator.Application.Settings;
using ContainerForest.Aggregator.Domain.Audit;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Notifications;

namespace ContainerForest.Aggregator.Application.Access;

/// <summary>
/// How people sign in (ADR-0019), set from the panel or with <c>CF_SIGNIN__REQUIRETWOFACTORFORADMINISTRATORS</c> and
/// <c>CF_SIGNIN__REQUIRETWOFACTORAFTERDISCORD</c>.
/// </summary>
public sealed class SignInSettingsSection : SettingsSection
{
    public const string SectionName = "SignIn";
    public const string RequireTwoFactorForAdministratorsField = "RequireTwoFactorForAdministrators";
    public const string RequireTwoFactorAfterDiscordField = "RequireTwoFactorAfterDiscord";

    public static readonly SignInSettingsSection Instance = new();

    public override string Name => SectionName;

    public override IReadOnlyList<SettingField> Fields { get; } =
    [
        new(RequireTwoFactorForAdministratorsField, Default: "false", Normalize: NormalizeFlag),
        new(RequireTwoFactorAfterDiscordField, Default: "false", Normalize: NormalizeFlag),
    ];

    private static string NormalizeFlag(string value) =>
        bool.TryParse(value, out var flag)
            ? flag ? "true" : "false"
            : throw new DomainValidationException("The value must be true or false.");
}

/// <summary>The typed view of <see cref="SignInSettingsSection"/>.</summary>
/// <param name="RequireTwoFactorForAdministrators">
/// An administrator who signed in with a password alone (or through Discord, with <paramref name="RequireTwoFactorAfterDiscord"/>)
/// gets a session that can only set up an authenticator app. A passkey sign-in is two factors already.
/// </param>
/// <param name="RequireTwoFactorAfterDiscord">
/// A Discord sign-in counts as a password: an account with an authenticator app is asked for its code. Off by default:
/// Discord's own two-factor protects the account.
/// </param>
public sealed record SignInSettings(bool RequireTwoFactorForAdministrators, bool RequireTwoFactorAfterDiscord = false)
{
    public static SignInSettings From(ResolvedSettings settings) => new(
        settings[SignInSettingsSection.RequireTwoFactorForAdministratorsField].Value == "true",
        settings[SignInSettingsSection.RequireTwoFactorAfterDiscordField].Value == "true");
}

/// <summary>
/// The sign-in settings for administrators. A change needs a fresh identity confirmation (ADR-0013): turning the
/// requirement off with a stolen session would make the system weaker for every administrator.
/// </summary>
public sealed class SignInSettingsService(
    CurrentActor currentActor,
    SettingsStore store,
    IdentityConfirmations confirmations,
    ISystemNotifier notifier,
    IAuditTrail audit)
{
    private static SignInSettingsSection Section => SignInSettingsSection.Instance;

    /// <summary>For any signed-in user's own state; the values are not secret.</summary>
    public async Task<SignInSettings> CurrentAsync(CancellationToken cancellationToken) =>
        SignInSettings.From(await store.ResolveAsync(Section, cancellationToken));

    public async Task<ResolvedSettings> GetAsync(CancellationToken cancellationToken)
    {
        await currentActor.GetAdministratorAsync(cancellationToken);
        return await store.ResolveAsync(Section, cancellationToken);
    }

    /// <exception cref="IdentityConfirmationRequiredException">The confirmation is missing or not valid.</exception>
    public Task<ResolvedSettings> UpdateAsync(SignInSettings settings, string? confirmation, CancellationToken cancellationToken)
    {
        var draft = new AuditDraft(AuditAction.SettingsChanged, new SettingsAuditTarget(Section.Name));
        return audit.RunAsync(draft, async () =>
        {
            var actor = await currentActor.GetAdministratorAsync(cancellationToken);
            confirmations.Require(confirmation, actor.Id);
            var changes = new Dictionary<string, string?>
            {
                [SignInSettingsSection.RequireTwoFactorForAdministratorsField] = settings.RequireTwoFactorForAdministrators ? "true" : "false",
                [SignInSettingsSection.RequireTwoFactorAfterDiscordField] = settings.RequireTwoFactorAfterDiscord ? "true" : "false",
            };
            var changed = await store.SaveAsync(Section, changes, cancellationToken);
            draft.Details = changed.Count > 0 ? $"Changed: {string.Join(", ", changed)}" : "Nothing changed";
            if (changed.Count > 0)
            {
                await notifier.NotifyAsync(
                    NotificationEventKinds.AccountSecurityChanged,
                    $"Sign-in settings changed by {actor.User.UserName}: " +
                    $"two-factor for administrators {(settings.RequireTwoFactorForAdministrators ? "required" : "not required")}, " +
                    $"code after Discord {(settings.RequireTwoFactorAfterDiscord ? "required" : "not required")}");
            }

            return await store.ResolveAsync(Section, cancellationToken);
        }, cancellationToken);
    }
}
