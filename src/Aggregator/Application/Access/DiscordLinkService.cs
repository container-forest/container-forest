using ContainerForest.Aggregator.Application.Common;
using ContainerForest.Aggregator.Domain.Access;
using ContainerForest.Aggregator.Domain.Common;

namespace ContainerForest.Aggregator.Application.Access;

public interface IDiscordLinkRepository : IRepository<DiscordLink, UserId>
{
    Task<DiscordLink?> FindByDiscordUserAsync(ulong discordUserId, CancellationToken cancellationToken);

    Task<DiscordLink?> FindByCodeHashAsync(string codeHash, CancellationToken cancellationToken);
}

public sealed record DiscordLinkStatus(bool Linked, string? DiscordUserName, DateTimeOffset? LinkedAt);

public sealed record DiscordLinkCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// Linking Discord accounts to users. The panel side (status, codes, unlinking) acts as the current user;
/// the bot side is called by the bot gateway after it has checked the bot's token.
/// </summary>
public sealed class DiscordLinkService(
    CurrentActor currentActor,
    IDiscordLinkRepository links,
    IUserRepository users,
    IUnitOfWork unitOfWork,
    TimeProvider time)
{
    public async Task<DiscordLinkStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var user = await currentActor.GetAsync(cancellationToken);
        var link = await links.FindAsync(user.Id, cancellationToken);
        return link is { IsLinked: true }
            ? new DiscordLinkStatus(true, link.DiscordUserName, link.LinkedAt)
            : new DiscordLinkStatus(false, null, null);
    }

    /// <summary>A new one-time code for the current user; the previous code stops working.</summary>
    public async Task<DiscordLinkCode> IssueCodeAsync(CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        var link = await links.FindAsync(user.Id, cancellationToken);
        if (link is null)
        {
            link = DiscordLink.For(user.Id);
            links.Add(link);
        }

        var code = link.IssueCode(time.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DiscordLinkCode(code, link.PendingCodeExpiresAt!.Value);
    }

    public async Task UnlinkAsync(CancellationToken cancellationToken)
    {
        var user = await currentActor.GetSignedInUserAsync(cancellationToken);
        if (await links.FindAsync(user.Id, cancellationToken) is { IsLinked: true } link)
        {
            link.Unlink();
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Completes a link from the bot. A Discord account already linked to someone else moves.</summary>
    /// <exception cref="DomainValidationException">The code is unknown, used or expired.</exception>
    /// <exception cref="AccessDeniedException">The user who issued the code is deactivated.</exception>
    public async Task<User> LinkAsync(ulong discordUserId, string discordUserName, string code, CancellationToken cancellationToken)
    {
        // Unknown and expired codes get the same answer, so guessing learns nothing.
        var link = await links.FindByCodeHashAsync(DiscordLink.HashCode(code), cancellationToken)
            ?? throw new DomainValidationException("The link code is invalid or has expired; get a new one in the panel.");

        var user = await users.GetAsync(link.Id, cancellationToken);
        if (!user.IsActive)
        {
            throw new AccessDeniedException($"User '{user.UserName}' is deactivated.");
        }

        if (await links.FindByDiscordUserAsync(discordUserId, cancellationToken) is { } previous && !previous.Equals(link))
        {
            // Saved separately: the Discord id is unique, and the new link must not be written before the old one is gone.
            previous.Unlink();
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        link.CompleteLink(discordUserId, discordUserName, time.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return user;
    }

    /// <summary>The user linked to the Discord account, or null when it is not linked.</summary>
    public async Task<User?> FindLinkedUserAsync(ulong discordUserId, CancellationToken cancellationToken) =>
        await links.FindByDiscordUserAsync(discordUserId, cancellationToken) is { } link
            ? await users.FindAsync(link.Id, cancellationToken)
            : null;
}
