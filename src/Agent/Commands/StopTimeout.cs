namespace ContainerForest.Agent.Commands;

internal static class StopTimeout
{
    /// <summary>Converts the protocol's timeout_seconds; 0 means "kill at once", as the aggregator allows it.</summary>
    /// <exception cref="InvalidCommandArgumentException">The value is negative.</exception>
    public static TimeSpan FromSeconds(int seconds) =>
        seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new InvalidCommandArgumentException("Stop timeout must not be negative.");
}
