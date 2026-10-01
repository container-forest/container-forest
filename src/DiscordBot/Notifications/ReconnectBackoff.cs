namespace ContainerForest.DiscordBot.Notifications;

/// <summary>
/// Delay before reopening the notification stream: doubles from <see cref="Initial"/> up to <see cref="Max"/>,
/// with jitter so several bots do not reconnect in step. A stream that lived for a while resets it.
/// </summary>
public sealed class ReconnectBackoff(TimeSpan initial, TimeSpan max)
{
    /// <summary>A stream open at least this long counts as healthy: the next failure starts from <see cref="Initial"/> again.</summary>
    public static readonly TimeSpan HealthyStreamAge = TimeSpan.FromMinutes(1);

    private int failures;

    public ReconnectBackoff()
        : this(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60))
    {
    }

    public TimeSpan Initial { get; } = initial;

    public TimeSpan Max { get; } = max;

    /// <summary>The delay after a stream that ended after <paramref name="streamAge"/>.</summary>
    public TimeSpan Next(TimeSpan streamAge)
    {
        if (streamAge >= HealthyStreamAge)
        {
            failures = 0;
        }

        var exponential = Initial.TotalMilliseconds * Math.Pow(2, Math.Min(failures, 16));
        failures++;
        var capped = Math.Min(exponential, Max.TotalMilliseconds);

        // ±20 %, never above the cap.
        var jittered = capped * (0.8 + (0.4 * Random.Shared.NextDouble()));
        return TimeSpan.FromMilliseconds(Math.Min(jittered, Max.TotalMilliseconds));
    }
}
