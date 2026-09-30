using ContainerForest.Aggregator.Application.Deployments;
using ContainerForest.Aggregator.Infrastructure.Deployments;

namespace ContainerForest.Aggregator.Infrastructure.Tests.Deployments;

public class YamlComposeParserTests
{
    private readonly YamlComposeParser parser = new();

    [Fact]
    public void Reads_image_build_and_extends_of_each_service()
    {
        var compose = parser.Parse("""
            services:
              web:
                image: nginx:1.27
                ports: ["80:80"]
              api:
                build: ./api
              worker:
                extends:
                  service: api
            """);

        Assert.Equal(
            [
                new ComposeService("web", "nginx:1.27", HasBuild: false, Extends: false),
                new ComposeService("api", null, HasBuild: true, Extends: false),
                new ComposeService("worker", null, HasBuild: false, Extends: true),
            ],
            compose.Services);
    }

    [Fact]
    public void Follows_merge_keys_and_anchors()
    {
        var compose = parser.Parse("""
            x-app: &app
              image: ghcr.io/org/app:2.0
              restart: unless-stopped
            services:
              web:
                <<: *app
                command: web
              jobs:
                <<: [*app]
                image: ghcr.io/org/jobs:1.0
            """);

        Assert.Equal("ghcr.io/org/app:2.0", compose.Services[0].Image);
        Assert.Equal("ghcr.io/org/jobs:1.0", compose.Services[1].Image);
    }

    [Fact]
    public void Syntax_errors_say_where()
    {
        var error = Assert.Throws<DeploymentFailedException>(() =>
            parser.Parse("services:\n  web:\n    image: nginx:1.27\n  web:\n    image: nginx:1.28\n"));

        Assert.Contains("line 4", error.Message);
    }

    [Theory]
    [InlineData("services: [web]")]
    [InlineData("name: demo")]
    [InlineData("- just a list")]
    [InlineData("")]
    [InlineData("services:\n  web: nginx")]
    [InlineData("services:\n  web:\n    image: [unclosed")]
    public void Rejects_files_without_a_usable_services_section(string yaml) =>
        Assert.Throws<DeploymentFailedException>(() => parser.Parse(yaml));
}
