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
}
