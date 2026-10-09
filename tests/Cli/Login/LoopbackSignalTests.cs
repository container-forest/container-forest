using System.Net;
using ContainerForest.Cli.Login;

namespace ContainerForest.Cli.Tests.Login;

public class LoopbackSignalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Browser_coming_back_wakes_the_cli_with_the_decision()
    {
        using var signal = LoopbackSignal.Start();
        using var browser = new HttpClient();

        var response = await browser.GetAsync($"http://127.0.0.1:{signal.Port}/done?result=denied", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("denied", await signal.Decision.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Other_paths_are_not_found_and_do_not_wake_the_cli()
    {
        using var signal = LoopbackSignal.Start();
        using var browser = new HttpClient();

        var response = await browser.GetAsync($"http://127.0.0.1:{signal.Port}/favicon.ico", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(signal.Decision.IsCompleted);
    }

    [Fact]
    public void Listens_on_loopback_only()
    {
        using var signal = LoopbackSignal.Start();

        Assert.InRange(signal.Port, 1, 65535);
    }
}
