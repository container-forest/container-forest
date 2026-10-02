using System.Threading.Channels;

namespace ContainerForest.Aggregator.Application.Notifications;

/// <summary>
/// Wakes the bot's notification stream when new notifications are saved, so it does not poll the database.
/// One stream at a time: a new subscription ends the previous one (bot.proto, WatchNotifications).
/// </summary>
/// <remarks>The database stays the source of truth: a subscriber rescans it on start and after each wake-up.</remarks>
public sealed class NotificationFeed
{
    private readonly Lock gate = new();
    private Subscription? current;

    /// <param name="callCancelled">Ends the subscription when the stream's call ends.</param>
    public Subscription Subscribe(CancellationToken callCancelled)
    {
        var next = new Subscription(this, callCancelled);
        lock (gate)
        {
            current?.Replace();
            current = next;
        }

        return next;
    }

    /// <summary>Tells the current subscriber, if any, that there is something new.</summary>
    public void Publish()
    {
        lock (gate)
        {
            current?.Signal();
        }
    }

    private void Leave(Subscription subscription)
    {
        lock (gate)
        {
            if (current == subscription)
            {
                current = null;
            }

            subscription.Close();
        }
    }

    /// <summary>One stream's view of the feed. Dispose it when the stream ends.</summary>
    public sealed class Subscription : IDisposable
    {
        private readonly NotificationFeed feed;
        private readonly CancellationTokenSource ended;

        // One pending wake-up is enough: the subscriber reads everything new after each one.
        private readonly Channel<bool> signals = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

        private bool closed;

        internal Subscription(NotificationFeed feed, CancellationToken callCancelled)
        {
            this.feed = feed;
            ended = CancellationTokenSource.CreateLinkedTokenSource(callCancelled);
        }

        /// <summary>Cancelled when the call ends or a newer subscription replaces this one.</summary>
        public CancellationToken Ended => ended.Token;

        /// <summary>Whether a newer subscription ended this one.</summary>
        public bool WasReplaced { get; private set; }

        /// <summary>Waits until something is published or <paramref name="timeout"/> passes, whichever is first.</summary>
        /// <exception cref="OperationCanceledException">The subscription has ended.</exception>
        public async Task WaitAsync(TimeSpan timeout)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ended);
            wait.CancelAfter(timeout);
            try
            {
                await signals.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!Ended.IsCancellationRequested)
            {
                // Timed out: the caller rescans anyway.
            }
        }

        public void Dispose() => feed.Leave(this);

        // The methods below run under the feed's lock.

        internal void Signal() => signals.Writer.TryWrite(true);

        internal void Replace()
        {
            if (!closed)
            {
                WasReplaced = true;
                ended.Cancel();
            }
        }

        internal void Close()
        {
            if (!closed)
            {
                closed = true;
                ended.Dispose();
            }
        }
    }
}
