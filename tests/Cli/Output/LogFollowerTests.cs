using ContainerForest.Cli.Api.Generated.Models;
using ContainerForest.Cli.Output;

namespace ContainerForest.Cli.Tests.Output;

public class LogFollowerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Lines_repeated_at_the_bound_of_since_are_shown_once()
    {
        var follower = new LogFollower();
        var first = follower.Accept([Line(T0, "a"), Line(T0.AddSeconds(1), "b")]);

        // The next poll asks from the newest time and gets that line again, plus what is new.
        var second = follower.Accept([Line(T0.AddSeconds(1), "b"), Line(T0.AddSeconds(1), "c"), Line(T0.AddSeconds(2), "d")]);

        Assert.Equal(["a", "b"], first.Select(l => l.Text));
        Assert.Equal(["c", "d"], second.Select(l => l.Text));
        Assert.Equal(T0.AddSeconds(2), follower.Since);
    }

    [Fact]
    public void Same_text_on_another_stream_is_another_line()
    {
        var follower = new LogFollower();
        follower.Accept([Line(T0, "x")]);

        var next = follower.Accept([Line(T0, "x"), Line(T0, "x", LogStream.Stderr)]);

        Assert.Single(next);
        Assert.Equal(LogStream.Stderr, next[0].Stream);
    }

    [Fact]
    public void Lines_without_a_time_are_always_shown()
    {
        var follower = new LogFollower();

        Assert.Equal(2, follower.Accept([new ContainerLogLineResponse { Text = "a" }, new ContainerLogLineResponse { Text = "a" }]).Count);
        Assert.Null(follower.Since);
    }

    private static ContainerLogLineResponse Line(DateTimeOffset at, string text, LogStream stream = LogStream.Stdout) =>
        new() { At = at, Text = text, Stream = stream };
}
