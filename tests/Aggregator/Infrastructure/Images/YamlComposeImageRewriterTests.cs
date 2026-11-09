using ContainerForest.Aggregator.Application.Images;
using ContainerForest.Aggregator.Domain.Common;
using ContainerForest.Aggregator.Domain.Images;
using ContainerForest.Aggregator.Infrastructure.Images;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Images;

public class YamlComposeImageRewriterTests
{
    private const string Image = "gitea.example.com/team/app/app";

    private readonly YamlComposeImageRewriter rewriter = new();

    [Fact]
    public void Changes_only_the_image_value_and_keeps_every_other_byte()
    {
        const string yaml = """
            # the bot
            services:
              bot:
                image: gitea.example.com/team/app/app:main-1111111   # pinned by CI
                restart: unless-stopped
              migrate:
                image: "gitea.example.com/team/app/app:main-1111111"
                command: ["migrate"]
              db:
                image: postgres:17
            """;

        var result = rewriter.Rewrite(yaml, Policy(), "main-2222222");

        Assert.Equal(yaml.Replace("main-1111111", "main-2222222", StringComparison.Ordinal), result.Yaml);
        Assert.Equal(
            [new ImageChange("bot", $"{Image}:main-1111111", $"{Image}:main-2222222"), new ImageChange("migrate", $"{Image}:main-1111111", $"{Image}:main-2222222")],
            result.Changes);
    }

    [Fact]
    public void Keeps_single_quotes_and_windows_line_endings()
    {
        var yaml = "services:\r\n  bot:\r\n    image: 'gitea.example.com/team/app/app:main-1111111'\r\n    restart: always\r\n";

        var result = rewriter.Rewrite(yaml, Policy(), "main-2222222");

        Assert.Equal(yaml.Replace("main-1111111", "main-2222222", StringComparison.Ordinal), result.Yaml);
    }

    [Fact]
    public void Writes_the_digest_and_replaces_one_that_was_there()
    {
        const string yaml = "services:\n  bot:\n    image: gitea.example.com/team/app/app:main-1111111@sha256:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" + "\n";
        var digest = "sha256:" + new string('b', 64);

        var result = rewriter.Rewrite(yaml, Policy(pinDigest: true), $"main-2222222@{digest}");

        Assert.Equal($"services:\n  bot:\n    image: {Image}:main-2222222@{digest}\n", result.Yaml);
    }

    [Fact]
    public void Updates_only_the_listed_services()
    {
        const string yaml = "services:\n  bot:\n    image: gitea.example.com/team/app/app:a1\n  canary:\n    image: gitea.example.com/team/app/app:a1\n";

        var result = rewriter.Rewrite(yaml, Policy(services: ["canary"]), "b2");

        Assert.Equal("services:\n  bot:\n    image: gitea.example.com/team/app/app:a1\n  canary:\n    image: gitea.example.com/team/app/app:b2\n", result.Yaml);
        Assert.Equal("canary", Assert.Single(result.Changes).Service);
    }

    [Fact]
    public void Matches_the_image_exactly_not_by_a_prefix()
    {
        const string yaml = "services:\n  other:\n    image: gitea.example.com/team/app/app-worker:a1\n";

        var error = Assert.Throws<ImagePolicyException>(() => rewriter.Find(yaml, Policy()));

        Assert.Contains("No service", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_docker_hub_images_however_they_are_written()
    {
        const string yaml = "services:\n  a:\n    image: nginx:1.27\n  b:\n    image: docker.io/library/nginx:1.27\n  c:\n    image: index.docker.io/library/nginx:1.27\n";
        var policy = ImagePolicy.Create(StackId.New(), ImageName.Parse("nginx"), Settings(@"^1\.[0-9]+\z"), UserId.New(), false, DateTimeOffset.UnixEpoch);

        var result = rewriter.Rewrite(yaml, policy, "1.28");

        Assert.Equal("services:\n  a:\n    image: nginx:1.28\n  b:\n    image: docker.io/library/nginx:1.28\n  c:\n    image: index.docker.io/library/nginx:1.28\n", result.Yaml);
    }

    [Theory]
    [InlineData("services:\n  bot:\n    image: gitea.example.com/team/app/app:${TAG}\n", "variables")]
    [InlineData("x-app: &app\n  image: gitea.example.com/team/app/app:a1\nservices:\n  bot:\n    <<: *app\n", "merge key")]
    [InlineData("x: &img gitea.example.com/team/app/app:a1\nservices:\n  bot:\n    image: *img\n", "anchor or alias")]
    [InlineData("services:\n  bot:\n    image: &img gitea.example.com/team/app/app:a1\n  other:\n    image: *img\n", "anchor or alias")]
    [InlineData("services:\n  bot:\n    image: >-\n      gitea.example.com/team/app/app:a1\n", "block")]
    public void Refuses_values_it_cannot_change_in_place(string yaml, string reason)
    {
        var error = Assert.Throws<ImagePolicyException>(() => rewriter.Find(yaml, Policy()));

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignores_services_of_other_images_that_use_variables()
    {
        const string yaml = "services:\n  bot:\n    image: gitea.example.com/team/app/app:a1\n  side:\n    image: ${REGISTRY}/side:1\n";

        Assert.Equal("bot", Assert.Single(rewriter.Find(yaml, Policy())).Service);
    }

    [Fact]
    public void Applies_recorded_changes_and_refuses_ones_that_do_not_fit()
    {
        const string yaml = "services:\n  bot:\n    image: \"gitea.example.com/team/app/app:a1\" # note\n";
        var change = new ImageChange("bot", $"{Image}:a1", $"{Image}:b2");

        Assert.Equal("services:\n  bot:\n    image: \"gitea.example.com/team/app/app:b2\" # note\n", rewriter.Apply(yaml, [change]));
        Assert.Null(rewriter.Apply(yaml, [change with { From = $"{Image}:zz" }]));
        Assert.Null(rewriter.Apply(yaml, [change with { Service = "missing" }]));
        Assert.Null(rewriter.Apply("not: [valid", [change]));
    }

    private static ImagePolicy Policy(bool pinDigest = false, IReadOnlyList<string>? services = null) =>
        ImagePolicy.Create(StackId.New(), ImageName.Parse(Image), Settings(@"^[a-z0-9-]+\z", services, pinDigest), UserId.New(), false, DateTimeOffset.UnixEpoch);

    private static ImagePolicySettings Settings(string pattern, IReadOnlyList<string>? services = null, bool pinDigest = false) =>
        new(pattern, ImageTagOrder.ImageCreated, services ?? [], PinDigest: pinDigest);
}
