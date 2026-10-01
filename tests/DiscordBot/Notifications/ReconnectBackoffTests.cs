extern alias bot;

using bot::ContainerForest.DiscordBot.Notifications;

namespace ContainerForest.DiscordBot.Tests.Notifications;

public sealed class ReconnectBackoffTests
{
    [Fact]
    public void Delay_DoublesWithJitter_UpToTheCap()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

        var delays = Enumerable.Range(0, 8).Select(_ => backoff.Next(TimeSpan.Zero).TotalSeconds).ToList();

        Assert.InRange(delays[0], 0.8, 1.2);
        Assert.InRange(delays[1], 1.6, 2.4);
        Assert.InRange(delays[2], 3.2, 4.8);
        Assert.All(delays, d => Assert.InRange(d, 0.8, 10));
        Assert.InRange(delays[^1], 8, 10);
    }

    [Fact]
    public void HealthyStream_StartsOverFromTheInitialDelay()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        for (var i = 0; i < 5; i++)
        {
            backoff.Next(TimeSpan.Zero);
        }

        Assert.InRange(backoff.Next(ReconnectBackoff.HealthyStreamAge).TotalSeconds, 0.8, 1.2);
    }
}
