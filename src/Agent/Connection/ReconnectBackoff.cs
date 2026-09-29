namespace ContainerForest.Agent.Connection;

/// <summary>
/// Delays between connection attempts: exponential up to <see cref="MaxDelay"/>, with jitter so that agents
/// do not all reconnect at the same moment after the aggregator restarts.
/// </summary>
public sealed class ReconnectBackoff(Random random)
{
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>A session that lived this long was healthy, so the next failure starts from the base delay again.</summary>
    public static readonly TimeSpan StableSession = TimeSpan.FromMinutes(1);

    // 2^6 s already exceeds MaxDelay; capping the exponent avoids overflow after many failures.
    private const int MaxExponent = 6;

    public int Attempts { get; private set; }

    /// <summary>The delay before the next attempt: a random value in [cap / 2, cap], cap = min(BaseDelay · 2^attempts, MaxDelay).</summary>
    public TimeSpan NextDelay()
    {
        var cap = TimeSpan.FromTicks(Math.Min(BaseDelay.Ticks << Math.Min(Attempts, MaxExponent), MaxDelay.Ticks));
        Attempts++;
        return cap / 2 + cap / 2 * random.NextDouble();
    }

    /// <summary>Called when a connection ends; a long-lived one resets the attempt counter.</summary>
    public void SessionEnded(TimeSpan lifetime)
    {
        if (lifetime >= StableSession)
        {
            Reset();
        }
    }

    public void Reset() => Attempts = 0;
}
