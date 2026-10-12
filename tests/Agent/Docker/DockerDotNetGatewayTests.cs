using ContainerForest.Agent.Docker;

namespace ContainerForest.Agent.Tests.Docker;

public class DockerDotNetGatewayTests
{
    [Theory]
    [InlineData("2026-10-02T12:00:00.250Z", "1790942400.250000000")]
    [InlineData("2026-10-02T12:00:00Z", "1790942400.000000000")]
    [InlineData("1970-01-01T00:00:00.007Z", "0.007000000")]
    public void Since_keeps_milliseconds(string since, string expected)
    {
        Assert.Equal(expected, DockerDotNetGateway.FormatSince(DateTimeOffset.Parse(since)));
    }

    [Theory]
    [InlineData("die", "137", 137)]
    [InlineData("die", "0", 0)]
    [InlineData("die", "-1", -1)]
    [InlineData("die", "", null)]
    [InlineData("die", "x", null)]
    [InlineData("stop", "0", null)]
    public void Exit_code_is_read_from_a_die_only(string action, string exitCode, int? expected)
    {
        var attributes = new Dictionary<string, string> { ["name"] = "web-app-1", ["exitCode"] = exitCode };

        Assert.Equal(expected, DockerDotNetGateway.ExitCodeOf(action, attributes));
    }

    [Fact]
    public void Die_without_the_attribute_has_no_exit_code()
    {
        Assert.Null(DockerDotNetGateway.ExitCodeOf("die", new Dictionary<string, string>()));
        Assert.Null(DockerDotNetGateway.ExitCodeOf("die", null));
    }
}
