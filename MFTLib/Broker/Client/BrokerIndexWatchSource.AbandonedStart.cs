namespace MFTLib;

/// <summary>
///     The half of the watch source that owns a start its caller stopped waiting for. The StartWatch
///     send is never cancelled once it is issued, so a start cancelled while that send is blocked (on
///     the client's arm-ordering gate or on the pipe write) throws at once and leaves the send running.
///     Whatever that send starts is torn down here, and the stream stays claimed until it is, so the
///     next start on this source cannot put its own StartWatch on the wire ahead of the teardown.
///     The teardown's stop follows the same generation rule as every other stop: a later start
///     cancels its wait for the broker's EndWatchAck, which is safe because that acknowledgement
///     names the abandoned watch's generation and cannot end the later one. A teardown that fails
///     for any other reason leaves this source failed for good.
/// </summary>
public sealed partial class BrokerIndexWatchSource
{
    // The teardown of the most recent start abandoned mid-send. Only meaningful while the stream
    // is claimed; a claim clears it, so a completed one left behind never parks a later start.
    Task? _abandonedStartRetirement;

    // Bounds the teardown's wait for the broker's acknowledgement. Only a later claim cancels it.
    // Never disposed: it has no timer and links nothing, and a claim may cancel it after its
    // teardown has finished.
    CancellationTokenSource? _abandonedStartRetirementCancellation;

    // Why the last abandoned start could not be torn down cleanly. Set once, never cleared, and
    // checked before any claim is granted, whether or not a start was waiting when it was set.
    Exception? _abandonedStartRetirementFailure;

    /// <summary>
    ///     Claims this source's single stream. While an abandoned start is still being torn down the
    ///     claim is held for it, and this waits for that teardown, bounded by
    ///     <paramref name="cancellationToken" />, instead of rejecting the start: the caller stopped
    ///     waiting for the old start, so it cannot be expected to know when its send finishes. The
    ///     wait first cancels the teardown's wait for the broker's acknowledgement, so what remains
    ///     is the send finishing and the client-side teardown. A claim held by a running stream is
    ///     rejected at once.
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
            CancellationTokenSource? retirementCancellation;
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
                retirementCancellation = _abandonedStartRetirementCancellation;
            }

            // Outside the lock: cancelling runs the stop's continuation, which releases the stream.
            // The source is never disposed, so a teardown that already finished leaves it cancellable.
            retirementCancellation?.Cancel();

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
        var retirementCancellation = new CancellationTokenSource();
        var retirement = RetireAbandonedStartAsync(abandoned, retirementCancellation);
        lock (_streamLock)
        {
            _abandonedStartRetirement = retirement;
            _abandonedStartRetirementCancellation = retirementCancellation;
        }
    }

    /// <summary>
    ///     Lets the abandoned send finish, then ends the live watch it started the same way a running
    ///     stream ends, through <see cref="JournalBrokerClient.StopLiveWatchAsync" />, bounded by
    ///     <paramref name="retirementCancellation" />, which only a later claim cancels. A stop that
    ///     a later claim cut short is not a failure: the acknowledgement it stopped waiting for names
    ///     the abandoned watch's generation, which the later watch's demux ignores. Any other
    ///     teardown failure is recorded in <see cref="_abandonedStartRetirementFailure" /> rather
    ///     than thrown, so the task itself never faults and nothing depends on a later start to
    ///     observe it. The stream is released last, after the failure is recorded, so no claim can
    ///     slip in between the two.
    /// </summary>
    async Task RetireAbandonedStartAsync(PendingStartWatch abandoned, CancellationTokenSource retirementCancellation)
    {
        var retirementToken = retirementCancellation.Token;
        try
        {
            // A send that fails after its caller left has nobody to report to, and it started
            // nothing to stop: the client disarms a failed send's drives itself.
            await abandoned.Send.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (abandoned.Send.IsCompletedSuccessfully)
            {
                await abandoned.Client.StopLiveWatchAsync(retirementToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (retirementToken.IsCancellationRequested)
        {
            // A later claim superseded the wait for the acknowledgement; see the summary.
            _ = exception;
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
            lock (_streamLock)
            {
                if (ReferenceEquals(_abandonedStartRetirementCancellation, retirementCancellation))
                {
                    _abandonedStartRetirementCancellation = null;
                }
            }

            ReleaseStream();
        }
    }

    /// <summary>A StartWatch send and the client it was issued on, held together for its teardown.</summary>
    readonly record struct PendingStartWatch(JournalBrokerClient Client, Task Send);
}
