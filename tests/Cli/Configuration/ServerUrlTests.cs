using ContainerForest.Cli.Configuration;

namespace ContainerForest.Cli.Tests.Configuration;

public class ServerUrlTests
{
    [Theory]
    [InlineData("forest.example.com", "https://forest.example.com/")]
    [InlineData("https://forest.example.com", "https://forest.example.com/")]
    [InlineData("https://forest.example.com/", "https://forest.example.com/")]
    [InlineData("https://forest.example.com:8443/panel/", "https://forest.example.com:8443/panel/")]
    [InlineData("http://localhost:5199", "http://localhost:5199/")]
    [InlineData("http://127.0.0.1:5080", "http://127.0.0.1:5080/")]
    [InlineData("http://[::1]:5080", "http://[::1]:5080/")]
    public void Address_is_normalized(string typed, string expected) =>
        Assert.Equal(expected, ServerUrl.Parse(typed).ToString());

    [Theory]
    [InlineData("http://forest.example.com")]
    [InlineData("http://192.168.1.10")]
    public void Plain_http_to_another_machine_is_refused(string typed)
    {
        var refused = Assert.Throws<CliException>(() => ServerUrl.Parse(typed));
        Assert.Contains("https", refused.Message);
    }

    [Theory]
    [InlineData("ftp://forest.example.com")]
    [InlineData("https://user:pass@forest.example.com")]
    [InlineData("https://forest.example.com/?x=1")]
    [InlineData("https://")]
    public void Unusable_address_is_refused(string typed) =>
        Assert.Equal(ExitCodes.Usage, Assert.Throws<CliException>(() => ServerUrl.Parse(typed)).ExitCode);

    [Fact]
    public void Same_server_ignores_case_and_trailing_slash() =>
        Assert.True(ServerUrl.SameServer(ServerUrl.Parse("https://Forest.Example.com"), ServerUrl.Parse("forest.example.com/")));

    [Fact]
    public void Another_port_is_another_server() =>
        Assert.False(ServerUrl.SameServer(ServerUrl.Parse("https://forest.example.com"), ServerUrl.Parse("https://forest.example.com:8443")));

    [Theory]
    [InlineData("https://forest.example.com", "forest.example.com")]
    [InlineData("http://localhost:5199", "localhost:5199")]
    public void Context_name_is_the_host(string url, string expected) =>
        Assert.Equal(expected, ServerUrl.ContextName(ServerUrl.Parse(url)));
}
