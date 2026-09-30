using Microsoft.Extensions.Time.Testing;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A <see cref="FakeTimeProvider" /> that signals each timer it creates, keyed by the timer's due
///     time and the order timers with that due time were created, and records whether each has
///     fired. A test waits for the timer under test to exist, then advances the clock to either side
///     of its due time, instead of stepping the clock in a loop.
/// </summary>
internal sealed class TimerSignalingClock : FakeTimeProvider
{
    readonly Lock _gate = new();
    readonly Dictionary<(TimeSpan DueTime, int Occurrence), TaskCompletionSource<CreatedTimer>> _created = [];
    readonly Dictionary<TimeSpan, int> _counts = [];

    /// <summary>
    ///     Completes once the <paramref name="occurrence" />-th timer due after <paramref name="dueTime" />
    ///     has been created, before or after this call.
    /// </summary>
    public Task<CreatedTimer> TimerCreated(TimeSpan dueTime, int occurrence = 1)
    {
        lock (_gate)
        {
            return Signal(dueTime, occurrence).Task;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var created = new CreatedTimer();
        var timer = base.CreateTimer(callbackState =>
        {
            created.MarkFired();
            callback(callbackState);
        }, state, dueTime, period);
        TaskCompletionSource<CreatedTimer> signal;
        lock (_gate)
        {
            var occurrence = _counts.GetValueOrDefault(dueTime) + 1;
            _counts[dueTime] = occurrence;
            signal = Signal(dueTime, occurrence);
        }

        signal.TrySetResult(created);
        return timer;
    }

    TaskCompletionSource<CreatedTimer> Signal(TimeSpan dueTime, int occurrence)
    {
        if (!_created.TryGetValue((dueTime, occurrence), out var signal))
        {
            signal = new TaskCompletionSource<CreatedTimer>(TaskCreationOptions.RunContinuationsAsynchronously);
            _created[(dueTime, occurrence)] = signal;
        }

        return signal;
    }

    /// <summary>One timer the clock created. A fake clock fires timers inside <see cref="FakeTimeProvider.Advance" />.</summary>
    internal sealed class CreatedTimer
    {
        int _fired;

        public bool Fired => Volatile.Read(ref _fired) != 0;

        internal void MarkFired() => Interlocked.Exchange(ref _fired, 1);
    }
}
