using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Images;
using ContainerForest.Aggregator.Domain.Stacks;

namespace ContainerForest.Aggregator.Domain.Tests.Images;

public class ImagePolicyTests
{
    [Theory]
    [InlineData("nginx", "docker.io/library/nginx")]
    [InlineData("library/nginx", "docker.io/library/nginx")]
    [InlineData("docker.io/nginx", "docker.io/library/nginx")]
    [InlineData("index.docker.io/team/app", "docker.io/team/app")]
    [InlineData(" gitea.rup.rocks/lepterion/ananasik/ananasik ", "gitea.rup.rocks/lepterion/ananasik/ananasik")]
    [InlineData("localhost:5000/app", "localhost:5000/app")]
    public void Image_names_are_normalized_as_docker_resolves_them(string value, string expected) =>
        Assert.Equal(expected, ImageName.Parse(value).Value);

    [Theory]
    [InlineData("nginx:1.27")]
    [InlineData("nginx@sha256:aaaa")]
    [InlineData("${REGISTRY}/app")]
    [InlineData("ghcr.io/Org/App")]
    [InlineData("")]
    public void Image_name_without_a_tag_digest_or_variables_is_required(string value) =>
        Assert.Throws<DomainValidationException>(() => ImageName.Parse(value));

    [Theory]
    [InlineData("main-[0-9a-f]{7}")]
    [InlineData(@"^main-[0-9a-f]{7}$")]
    [InlineData(@"^main-[0-9a-f{7}\z")]
    public void Pattern_must_be_anchored_and_valid(string pattern) =>
        Assert.Throws<DomainValidationException>(() => Policy(pattern));

    [Fact]
    public void Timestamp_order_needs_the_ts_group() =>
        Assert.Throws<DomainValidationException>(() => Policy(@"^main-[0-9]{8}\z", ImageTagOrder.TagTimestamp));

    [Fact]
    public void Tags_match_whole_and_a_trailing_newline_does_not_pass()
    {
        var policy = Policy(@"^main-[0-9a-f]{7}\z");

        Assert.True(policy.Allows("main-7822307"));
        Assert.False(policy.Allows("main-7822307\n"));
        Assert.False(policy.Allows("xmain-7822307"));
        Assert.False(policy.Allows("feature-7822307"));
    }

    [Fact]
    public void Timestamps_compare_by_length_then_text()
    {
        var policy = Policy(@"^main-(?<ts>[0-9]+)-[0-9a-f]{7}\z", ImageTagOrder.TagTimestamp);

        Assert.True(policy.TryReadTimestamp("main-20261109120000-7822307", out var a));
        Assert.True(policy.TryReadTimestamp("main-9-0dd73f3", out var b));
        Assert.True(ImagePolicy.CompareTimestamps(a, b) > 0);
        Assert.True(ImagePolicy.CompareTimestamps("20261109", "20261108") > 0);
    }

    [Fact]
    public void Failures_are_told_once_and_the_registry_only_from_the_third()
    {
        var policy = Policy(@"^v[0-9]+\z");
        var now = DateTimeOffset.UnixEpoch;

        Assert.False(policy.RecordFailed("down", registryUnavailable: true, now));
        Assert.False(policy.RecordFailed("down", registryUnavailable: true, now));
        Assert.True(policy.RecordFailed("down", registryUnavailable: true, now));
        Assert.False(policy.RecordFailed("still down", registryUnavailable: true, now));

        policy.RecordChecked(now, "v2", null);
        Assert.True(policy.RecordFailed("push refused", registryUnavailable: false, now));
        Assert.Null(policy.Warning);
    }

    [Fact]
    public void Pin_must_match_the_pattern_and_a_new_pattern_must_keep_the_pin()
    {
        var policy = Policy(@"^v[0-9]+\z");

        Assert.Throws<DomainValidationException>(() => policy.Pin("latest", "alice", DateTimeOffset.UnixEpoch));
        policy.Pin("v3", "alice", DateTimeOffset.UnixEpoch);
        Assert.Throws<DomainValidationException>(() =>
            policy.Change(new ImagePolicySettings(@"^w[0-9]+\z", ImageTagOrder.ImageCreated, []), UserId.New(), false));

        policy.Unpin();
        Assert.False(policy.IsPinned);
    }

    [Fact]
    public void Update_record_needs_changes_and_another_parent()
    {
        var sha = CommitSha.Parse(new string('a', 40));
        var path = StackFilePath.Parse("compose.yaml");

        Assert.Throws<DomainValidationException>(() => ImageUpdateRecord.Create(
            ImageUpdateRecordId.New(), StackId.New(), ImagePolicyId.New(), sha, CommitSha.Parse(new string('b', 40)), path, "v2", null, [],
            ImageUpdateReason.Policy, null, null, DateTimeOffset.UnixEpoch));
        Assert.Throws<DomainValidationException>(() => ImageUpdateRecord.Create(
            ImageUpdateRecordId.New(), StackId.New(), ImagePolicyId.New(), sha, sha, path, "v2", null, [new ImageChange("a", "x:1", "x:2")],
            ImageUpdateReason.Policy, null, null, DateTimeOffset.UnixEpoch));
    }

    private static ImagePolicy Policy(string pattern, ImageTagOrder order = ImageTagOrder.ImageCreated) =>
        ImagePolicy.Create(StackId.New(), ImageName.Parse("app"), new ImagePolicySettings(pattern, order, []), UserId.New(), false, DateTimeOffset.UnixEpoch);
}
