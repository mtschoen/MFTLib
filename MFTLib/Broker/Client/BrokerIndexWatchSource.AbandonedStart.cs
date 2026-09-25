namespace MFTLib;

/// <summary>
///     The half of the watch source that owns a start its caller stopped waiting for. The StartWatch
///     send is never cancelled once it is issued, so a start cancelled while that send is blocked (on
///     the client's arm-ordering gate or on the pipe write) throws at once and leaves the send running.
///     Whatever that send starts is torn down here, and the stream stays claimed until it is, so the
///     next start on this source cannot put its own StartWatch on the wire ahead of the teardown.
///     A teardown that fails, including a stop that gave up waiting for the broker's EndWatchAck,
///     leaves this source failed for good: that acknowledgement may still be on its way, and a
///     later watch on the same connection would be the one it ends.
/// </summary>
public sealed partial class BrokerIndexWatchSource
{
    // The teardown of the most recent start abandoned mid-send. Only meaningful while the stream
    // is claimed; a claim clears it, so a completed one left behind never parks a later start.
    Task? _abandonedStartRetirement;

    // Why the last abandoned start could not be torn down cleanly. Set once, never cleared, and
    // checked before any claim is granted, whether or not a start was waiting when it was set.
    Exception? _abandonedStartRetirementFailure;

    /// <summary>
    ///     Claims this source's single stream. While an abandoned start is still being torn down the
    ///     claim is held for it, and this waits for that teardown, bounded by
    ///     <paramref name="cancellationToken" />, instead of rejecting the start: the caller stopped
    ///     waiting for the old start, so it cannot be expected to know when its send finishes. A
    ///     claim held by a running
    ///     stream is rejected at once, as it always was.
    ///     A teardown that failed fails this start and every later one with an
    ///     <see cref="InvalidOperationException" /> whose inner exception is that failure: its
    ///     connection is not known to be safe to watch on again, so the consumer needs a new
    ///     connection and a new source.
    /// </summary>
    async Task ClaimStreamAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task retirement;
            lock (_streamLock)
            {
                if (_abandonedStartRetirementFailure is { } failure)
                {
                    throw new InvalidOperationException(
                        "This watch source cannot start again: tearing down a start that was cancelled while " +
                        "its StartWatch frame was being sent failed, so its broker connection is not known to be " +
                        "safe to watch on. Create a new connection and watch source.", failure);
                }

                if (!_streamClaimed)
                {
                    _streamClaimed = true;
                    _abandonedStartRetirement = null;
                    return;
                }

                if (_abandonedStartRetirement is not { IsCompleted: false } pending)
                {
                    throw new InvalidOperationException(
                        "This watch source is already running a stream. One instance runs at most one at a time.");
                }

                retirement = pending;
            }

            await retirement.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     True when the send has not failed: it is still in flight, or it has put the StartWatch
    ///     frame on the wire and started the client's live watch, which something now has to stop.
    ///     A send that failed disarmed its own drives and started nothing.
    /// </summary>
    static bool IsStillInFlightOrSent(Task startWatchSend)
    {
        return !startWatchSend.IsCompleted || startWatchSend.IsCompletedSuccessfully;
    }

    void RetireAbandonedStart(PendingStartWatch abandoned)
    {
        var retirement = RetireAbandonedStartAsync(abandoned);
        lock (_streamLock)
        {
            _abandonedStartRetirement = retirement;
        }
    }

    /// <summary>
    ///     Lets the abandoned send finish, then ends the live watch it started the same way a running
    ///     stream ends, through <see cref="JournalBrokerClient.StopLiveWatchAsync" />, whose demux
    ///     reads the broker's EndWatchAck before it returns. That read is what keeps the
    ///     acknowledgement from reaching, and ending, the next watch on the same client. A stop that
    ///     timed out without reading it, or any other teardown failure, is recorded in
    ///     <see cref="_abandonedStartRetirementFailure" /> rather than thrown, so the task itself
    ///     never faults and nothing depends on a later start to observe it. The stream is released
    ///     last, after the failure is recorded, so no claim can slip in between the two.
    /// </summary>
    async Task RetireAbandonedStartAsync(PendingStartWatch abandoned)
    {
        try
        {
            // A send that fails after its caller left has nobody to report to, and it started
            // nothing to stop: the client disarms a failed send's drives itself.
            await abandoned.Send.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (abandoned.Send.IsCompletedSuccessfully)
            {
                await abandoned.Client.StopLiveWatchAsync().ConfigureAwait(false);
                if (abandoned.Client.LastStopTimedOut)
                {
                    throw new TimeoutException(
                        "The broker did not send EndWatchAck for the abandoned watch before the stop gave up " +
                        "waiting, so that acknowledgement could still arrive and end a later watch.");
                }
            }
        }
        catch (Exception teardownFailure)
        {
            lock (_streamLock)
            {
                _abandonedStartRetirementFailure = teardownFailure;
            }
        }
        finally
        {
            ReleaseStream();
        }
    }

    /// <summary>A StartWatch send and the client it was issued on, held together for its teardown.</summary>
    readonly record struct PendingStartWatch(JournalBrokerClient Client, Task Send);
}
