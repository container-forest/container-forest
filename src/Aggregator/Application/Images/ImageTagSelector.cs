using ContainerForest.Aggregator.Domain.Images;

namespace ContainerForest.Aggregator.Application.Images;

public enum ImageSelectionOutcome
{
    /// <summary><see cref="ImageSelection.Newest"/> is the newest tag the policy allows.</summary>
    Selected,

    /// <summary>No tag in the registry matches the pattern.</summary>
    NoCandidates,

    /// <summary>
    /// Not every candidate's creation time is known yet (<see cref="ImageTagOrder.ImageCreated"/>): until it is, the
    /// newest tag is unknown, and the image is not updated.
    /// </summary>
    CatalogIncomplete,

    /// <summary>The newest candidates are different images with the same creation time; the aggregator does not guess.</summary>
    Ambiguous,
}

/// <summary>A tag the policy allows, with what is known of it.</summary>
/// <param name="Timestamp">The tag's "ts" group, for <see cref="ImageTagOrder.TagTimestamp"/>.</param>
public sealed record ImageCandidate(string Tag, string? Digest, DateTimeOffset? Created, string? Timestamp);

/// <param name="Newest">Set for <see cref="ImageSelectionOutcome.Selected"/>.</param>
/// <param name="Problem">Why nothing was selected, in words for the policy's Editor.</param>
public sealed record ImageSelection(ImageSelectionOutcome Outcome, ImageCandidate? Newest, int CandidateCount, string? Problem = null);

/// <summary>
/// Picks the newest tag a policy allows (ADR-0015, item 2), without any I/O: the caller lists the tags and reads what
/// the order needs. Lexical order is never used: "main-&lt;sha&gt;" does not sort by time.
/// </summary>
public static class ImageTagSelector
{
    /// <summary>More candidates than this make <see cref="ImageTagOrder.ImageCreated"/> expensive; the panel warns.</summary>
    public const int ManyCandidates = 500;

    /// <summary>The registry's tags the policy allows, in the registry's order.</summary>
    public static IReadOnlyList<string> Candidates(ImagePolicy policy, IEnumerable<string> tags) =>
        tags.Where(policy.Allows).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>By the "ts" group of each tag; nothing but the tag list is needed.</summary>
    public static ImageSelection ByTimestamp(ImagePolicy policy, IReadOnlyList<string> candidates)
    {
        var dated = candidates
            .Select(tag => policy.TryReadTimestamp(tag, out var ts) ? new ImageCandidate(tag, null, null, ts) : null)
            .OfType<ImageCandidate>()
            .ToList();
        if (dated.Count == 0)
        {
            return new ImageSelection(ImageSelectionOutcome.NoCandidates, null, 0, NoTags(policy));
        }

        var newest = dated.Aggregate((a, b) => Compare(policy, a, b) >= 0 ? a : b);
        var tied = dated.Where(c => ImagePolicy.CompareTimestamps(c.Timestamp!, newest.Timestamp!) == 0).ToList();
        return tied.Count > 1
            ? new ImageSelection(
                ImageSelectionOutcome.Ambiguous, null, dated.Count,
                $"Tags {string.Join(", ", tied.Select(c => c.Tag).Take(3))} have the same timestamp {newest.Timestamp}.")
            : new ImageSelection(ImageSelectionOutcome.Selected, newest, dated.Count);
    }

    /// <summary>By the creation time of each candidate's image; every candidate must be in <paramref name="known"/>.</summary>
    public static ImageSelection ByCreated(
        ImagePolicy policy, IReadOnlyList<string> candidates, IReadOnlyDictionary<string, ImageTagCacheEntry> known)
    {
        if (candidates.Count == 0)
        {
            return new ImageSelection(ImageSelectionOutcome.NoCandidates, null, 0, NoTags(policy));
        }

        var missing = candidates.Count(tag => !known.ContainsKey(tag));
        if (missing > 0)
        {
            return new ImageSelection(
                ImageSelectionOutcome.CatalogIncomplete, null, candidates.Count,
                $"The creation time of {missing} of {candidates.Count} tags is not read yet; the next checks read the rest.");
        }

        var dated = candidates
            .Select(tag => known[tag])
            .Where(entry => entry.Created is not null)
            .Select(entry => new ImageCandidate(entry.Tag, entry.Digest, entry.Created, null))
            .ToList();
        if (dated.Count == 0)
        {
            return new ImageSelection(
                ImageSelectionOutcome.Ambiguous, null, candidates.Count,
                "No image of the allowed tags has a creation time; order the tags by a timestamp in the tag instead.");
        }

        var latest = dated.Max(c => c.Created!.Value);
        var newest = dated.Where(c => c.Created == latest).ToList();
        if (newest.Select(c => c.Digest).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return new ImageSelection(
                ImageSelectionOutcome.Ambiguous, null, candidates.Count,
                $"Tags {string.Join(", ", newest.Select(c => c.Tag).Take(3))} are different images created at the same time " +
                $"({latest:u}), as reproducible builds do; order the tags by a timestamp in the tag instead.");
        }

        // The same image under several tags: any of them is the newest; the last in the registry's order is kept.
        return new ImageSelection(ImageSelectionOutcome.Selected, newest[^1], candidates.Count);
    }

    /// <summary>
    /// Negative when <paramref name="left"/> is older than <paramref name="right"/>, by the policy's order. A candidate
    /// without what the order needs is older than any with it.
    /// </summary>
    public static int Compare(ImagePolicy policy, ImageCandidate left, ImageCandidate right)
    {
        if (policy.Order == ImageTagOrder.TagTimestamp)
        {
            return (left.Timestamp, right.Timestamp) switch
            {
                (null, null) => 0,
                (null, _) => -1,
                (_, null) => 1,
                var (l, r) => ImagePolicy.CompareTimestamps(l, r),
            };
        }

        return Nullable.Compare(left.Created, right.Created);
    }

    /// <summary>The candidates whose creation time is still to be read, at most <paramref name="limit"/>.</summary>
    /// <remarks>Newest-looking first: registries list tags in name order, so the end of the list is read first.</remarks>
    public static IReadOnlyList<string> ToRead(
        IReadOnlyList<string> candidates, IReadOnlyDictionary<string, ImageTagCacheEntry> known, int limit) =>
        candidates.Where(tag => !known.ContainsKey(tag)).Reverse().Take(limit).ToList();

    private static string NoTags(ImagePolicy policy) => $"No tag of {policy.Image} matches {policy.TagPattern}.";
}
