namespace ContainerForest.Aggregator.Domain.Images;

/// <summary>
/// What the aggregator learned about one tag of a registry repository: its digest and creation time. Shared by every
/// policy for the image, and kept in the database so a restart does not read every manifest again. Tags are assumed
/// not to move (ADR-0015): a tag pushed again under the same name keeps its cached values.
/// </summary>
public sealed class ImageTagCacheEntry
{
    public const int MaxDigestLength = 100;

    private ImageTagCacheEntry(string image, string tag, string digest, DateTimeOffset? created, DateTimeOffset fetchedAt)
    {
        Image = image;
        Tag = tag;
        Digest = digest;
        Created = created;
        FetchedAt = fetchedAt;
    }

    /// <summary><see cref="ImageName.Value"/> of the repository.</summary>
    public string Image { get; private init; }

    public string Tag { get; private init; }

    /// <summary>"sha256:…" of the manifest or index the tag points to.</summary>
    public string Digest { get; private init; }

    /// <summary>The "created" time of the image's configuration; null when the image has none.</summary>
    public DateTimeOffset? Created { get; private init; }

    public DateTimeOffset FetchedAt { get; private init; }

    public static ImageTagCacheEntry Create(ImageName image, string tag, string digest, DateTimeOffset? created, DateTimeOffset now) =>
        new(image.Value, tag, digest, created, now);
}
