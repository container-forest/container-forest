using ContainerForest.Agent.Connection;

namespace ContainerForest.Agent.Tests.Connection;

public class ReconnectBackoffTests
{
    [Fact]
    public void Delay_doubles_up_to_the_maximum()
    {
        var backoff = new ReconnectBackoff(new FixedRandom(1.0));

        var delays = Enumerable.Range(0, 10).Select(_ => backoff.NextDelay().TotalSeconds).ToArray();

        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60, 60, 60], delays);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.999)]
    public void Jitter_keeps_the_delay_between_half_and_full_cap(double random)
    {
        var backoff = new ReconnectBackoff(new FixedRandom(random));
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var cap = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 60));
            var delay = backoff.NextDelay();
            Assert.InRange(delay, cap / 2, cap);
        }
    }

    [Fact]
    public void Long_session_resets_the_attempts()
    {
        var backoff = new ReconnectBackoff(new FixedRandom(1.0));
        for (var i = 0; i < 5; i++)
        {
            backoff.NextDelay();
        }

        backoff.SessionEnded(TimeSpan.FromSeconds(59));
        Assert.Equal(5, backoff.Attempts);

        backoff.SessionEnded(TimeSpan.FromMinutes(1));
        Assert.Equal(0, backoff.Attempts);
        Assert.Equal(TimeSpan.FromSeconds(1), backoff.NextDelay());
    }

    [Fact]
    public void Many_failures_do_not_overflow()
    {
        var backoff = new ReconnectBackoff(new FixedRandom(1.0));
        for (var i = 0; i < 10_000; i++)
        {
            backoff.NextDelay();
        }

        Assert.Equal(ReconnectBackoff.MaxDelay, backoff.NextDelay());
    }
}
