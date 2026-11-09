using ContainerForest.Aggregator.Application.Images;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Images;

namespace ContainerForest.Aggregator.Application.Tests.Images;

public class ImageTagSelectorTests
{
    private static readonly ImageName Image = ImageName.Parse("app");
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Creation_time_decides_not_the_name()
    {
        var policy = Policy(@"^main-[0-9a-f]{7}\z");
        var candidates = ImageTagSelector.Candidates(policy, ["main-ffffff0", "main-0000001", "latest", "dev-1234567"]);
        var known = Known(("main-ffffff0", "sha256:a", T0), ("main-0000001", "sha256:b", T0.AddHours(1)));

        var selection = ImageTagSelector.ByCreated(policy, candidates, known);

        Assert.Equal(ImageSelectionOutcome.Selected, selection.Outcome);
        Assert.Equal("main-0000001", selection.Newest!.Tag);
        Assert.Equal(2, selection.CandidateCount);
    }

    [Fact]
    public void Unread_candidates_hold_the_update_back()
    {
        var policy = Policy(@"^v[0-9]+\z");

        var selection = ImageTagSelector.ByCreated(policy, ["v1", "v2"], Known(("v1", "sha256:a", T0)));

        Assert.Equal(ImageSelectionOutcome.CatalogIncomplete, selection.Outcome);
        Assert.Equal(["v2"], ImageTagSelector.ToRead(["v1", "v2"], Known(("v1", "sha256:a", T0)), 100));
    }

    [Fact]
    public void Different_images_created_at_the_same_time_are_not_guessed()
    {
        var policy = Policy(@"^v[0-9]+\z");

        var selection = ImageTagSelector.ByCreated(policy, ["v1", "v2"], Known(("v1", "sha256:a", T0), ("v2", "sha256:b", T0)));

        Assert.Equal(ImageSelectionOutcome.Ambiguous, selection.Outcome);
        Assert.Contains("same time", selection.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_image_under_two_tags_is_not_ambiguous()
    {
        var policy = Policy(@"^v[0-9]+\z");

        var selection = ImageTagSelector.ByCreated(policy, ["v1", "v2"], Known(("v1", "sha256:a", T0), ("v2", "sha256:a", T0)));

        Assert.Equal(ImageSelectionOutcome.Selected, selection.Outcome);
    }

    [Fact]
    public void Timestamp_in_the_tag_orders_without_any_manifest()
    {
        var policy = Policy(@"^main-(?<ts>[0-9]{8}T[0-9]{6})-[0-9a-f]{7}\z", ImageTagOrder.TagTimestamp);
        var candidates = ImageTagSelector.Candidates(policy, ["main-20261109T120000-7822307", "main-20261108T090000-0dd73f3", "main-7822307"]);

        var selection = ImageTagSelector.ByTimestamp(policy, candidates);

        Assert.Equal("main-20261109T120000-7822307", selection.Newest!.Tag);
        Assert.Equal(2, selection.CandidateCount);
    }

    [Fact]
    public void No_matching_tag_is_said_so()
    {
        var policy = Policy(@"^v[0-9]+\z");

        Assert.Equal(ImageSelectionOutcome.NoCandidates, ImageTagSelector.ByCreated(policy, [], Known()).Outcome);
        Assert.Equal(ImageSelectionOutcome.NoCandidates, ImageTagSelector.ByTimestamp(Policy(@"^(?<ts>[0-9]+)\z", ImageTagOrder.TagTimestamp), []).Outcome);
    }

    private static ImagePolicy Policy(string pattern, ImageTagOrder order = ImageTagOrder.ImageCreated) =>
        ImagePolicy.Create(StackId.New(), Image, new ImagePolicySettings(pattern, order, []), UserId.New(), false, T0);

    private static Dictionary<string, ImageTagCacheEntry> Known(params (string Tag, string Digest, DateTimeOffset Created)[] entries) =>
        entries.ToDictionary(e => e.Tag, e => ImageTagCacheEntry.Create(Image, e.Tag, e.Digest, e.Created, T0), StringComparer.Ordinal);
}
