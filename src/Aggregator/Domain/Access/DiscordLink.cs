using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Domain.Access;

/// <summary>
/// A user's link to a Discord account, which lets the Discord bot act for them. One per user; its id is the
/// user's id. The user gets a one-time code in the panel and types it in Discord to complete the link.
/// </summary>
/// <remarks>
/// A Discord account links to at most one user; the service moves the link when the same Discord account
/// links again. Only the hash of the pending code is stored.
/// </remarks>
public sealed class DiscordLink : Entity<UserId>
{
    public const int CodeLength = 8;
    public const int MaxDiscordUserNameLength = 64;

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private DiscordLink(UserId userId)
        : base(userId)
    {
    }

    // For EF Core.
    private DiscordLink() { }

    public ulong? DiscordUserId { get; private set; }

    /// <summary>Discord name at the time of linking; shown in the panel, never used for decisions.</summary>
    public string? DiscordUserName { get; private set; }

    public DateTimeOffset? LinkedAt { get; private set; }

    public string? PendingCodeHash { get; private set; }

    public DateTimeOffset? PendingCodeExpiresAt { get; private set; }

    public bool IsLinked => DiscordUserId is not null;

    public static DiscordLink For(UserId userId) => new(userId);

    /// <summary>Issues a new code, replacing any previous one. Returns the code; only its hash is kept.</summary>
    public string IssueCode(DateTimeOffset now)
    {
        var code = OneTimeCode.Generate(CodeLength);
        PendingCodeHash = HashCode(code);
        PendingCodeExpiresAt = now + CodeLifetime;
        return code;
    }

    /// <summary>Hash of a code as the user typed it: case, spaces and dashes do not matter.</summary>
    public static string HashCode(string code) => OneTimeCode.Hash(code);

    /// <summary>Links the Discord account with the pending code, which is used up.</summary>
    /// <exception cref="DomainValidationException">There is no pending code, or it has expired.</exception>
    public void CompleteLink(ulong discordUserId, string discordUserName, DateTimeOffset now)
    {
        if (PendingCodeHash is null || PendingCodeExpiresAt is not { } expiresAt || now >= expiresAt)
        {
            throw new DomainValidationException("The link code is invalid or has expired; get a new one in the panel.");
        }

        DiscordUserId = discordUserId;
        DiscordUserName = NormalizeDiscordUserName(discordUserName);
        LinkedAt = now;
        PendingCodeHash = null;
        PendingCodeExpiresAt = null;
    }

    /// <summary>Removes the link; the bot can no longer act for the user. A pending code stays valid.</summary>
    public void Unlink()
    {
        DiscordUserId = null;
        DiscordUserName = null;
        LinkedAt = null;
    }

    private static string NormalizeDiscordUserName(string name)
    {
        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length > MaxDiscordUserNameLength ? cleaned[..MaxDiscordUserNameLength] : cleaned;
    }
}
