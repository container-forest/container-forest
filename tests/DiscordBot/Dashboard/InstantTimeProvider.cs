namespace ContainerForest.DiscordBot.Tests.Dashboard;

/// <summary>
/// A clock whose timers fire at once: every delay moves <see cref="GetUtcNow"/> forward by its length, so a test
/// of polling runs in no time and can still check how long the code would have waited.
/// </summary>
internal sealed class InstantTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private long elapsedTicks;

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref elapsedTicks));

    public override DateTimeOffset GetUtcNow() => Start + Elapsed;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Add(ref elapsedTicks, dueTime.Ticks);

        // Fired on the thread pool, as a real timer: Task.Delay finishes setting up after CreateTimer returns.
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new FiredTimer();
    }

    private sealed class FiredTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
