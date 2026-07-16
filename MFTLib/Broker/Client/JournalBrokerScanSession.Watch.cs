namespace MFTLib;

// Watch half of the scan-to-watch session: starts and stops live watching on the
// same owned client the session scanned with, and hands out per-drive batch
// enumerables seeded from the scan's advanced cursors.
public sealed partial class JournalBrokerScanSession
{
    /// <summary>
    /// Begin live watching every drive in the session's watch cursor set - a scanned
    /// session's successfully armed drives (from <see cref="LatestScan"/>), or a warm
    /// session's supplied cursors - resuming each from its cursor. Legal only in
    /// <see cref="JournalBrokerSessionState.Parked"/>. Throws
    /// <see cref="InvalidOperationException"/> if already watching or if there is nothing
    /// to watch. On the scanned path the consumer never supplies cursors, so a volume or
    /// cursor mismatch is not representable.
    /// </summary>
    public async Task StartWatchAsync(CancellationToken cancellationToken = default)
    {
        EnsureOperable();
        IReadOnlyDictionary<string, UsnJournalCursor> watchCursors;
        lock (_stateLock)
        {
            if (_state != JournalBrokerSessionState.Parked)
                throw new InvalidOperationException("Live watch has already been started for this session");
            if (_operationInFlight)
                throw new InvalidOperationException("Another session operation is in progress");
            _operationInFlight = true;
            watchCursors = _watchCursors;
        }

        // Cleared inside the commit lock below on the success path (together with
        // State = Watching, so State == Watching never overlaps a still-true flag);
        // the finally only needs to clear it itself for a throw before that point.
        var committed = false;
        var transmissionStarted = false;
        try
        {
            if (watchCursors.Count == 0)
                throw new InvalidOperationException("No drives to watch");

            try
            {
                await _client.SendStartWatchAsync(
                    watchCursors,
                    () => transmissionStarted = true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (transmissionStarted)
            {
                // A cancelled in-progress write may have delivered a partial or full
                // StartWatch frame. The protocol boundary is ambiguous, so terminate
                // the owned client instead of advertising a retryable Parked session.
                await DisposeAsync().ConfigureAwait(false);
                throw;
            }
            var batchSource = _client.CreateBatchSource();

            // A Dispose or broker-death fault can land while the await above was in
            // flight; recheck under the lock and only commit Watching if the session is
            // still operable, so a terminal state already recorded elsewhere is never
            // resurrected back to a live one.
            lock (_stateLock)
            {
                EnsureOperableLocked();
                _batchSource = batchSource;
                _state = JournalBrokerSessionState.Watching;
                _operationInFlight = false;
                committed = true;
            }
        }
        finally
        {
            if (!committed)
                lock (_stateLock)
                    _operationInFlight = false;
        }
    }

    /// <summary>
    /// Yield live journal batches for one watched drive, resuming from its watch cursor
    /// (a scanned session's advanced cursor, or a warm session's supplied cursor). Legal
    /// only in <see cref="JournalBrokerSessionState.Watching"/>. Throws
    /// <see cref="ArgumentException"/> if <paramref name="driveLetter"/> is not among the
    /// session's watch cursors. The enumerable faults with
    /// <see cref="InvalidOperationException"/> if that drive's journal is invalidated
    /// mid-watch or the broker dies. One consumer per drive.
    /// </summary>
    public IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchDriveAsync(
        string driveLetter, CancellationToken cancellationToken = default)
    {
        EnsureOperable();

        // The Watching check, the cursor lookup, and the _batchSource read must
        // happen in one critical section: StopWatchAsync clears _batchSource under
        // the same lock, so splitting these into separate lock statements would let
        // a concurrent stop null the field between the check and the read.
        JournalBatchSource batchSource;
        UsnJournalCursor cursor;
        lock (_stateLock)
        {
            if (_state != JournalBrokerSessionState.Watching)
                throw new InvalidOperationException("Not currently watching; call StartWatchAsync first");
            if (!_watchCursors.TryGetValue(driveLetter, out cursor))
                throw new ArgumentException($"Drive '{driveLetter}' is not being watched", nameof(driveLetter));
            // StartWatchAsync always sets _batchSource together with State = Watching
            // under this same lock, so a null here means that invariant broke rather
            // than something a caller can act on - a clear diagnostic beats a silent
            // null-forgiving `!` (same rationale as BrokerFrame.RequireDrive/Message).
            batchSource = _batchSource
                ?? throw new InvalidOperationException("Watching state has no cached batch source");
        }

        return batchSource(driveLetter, cursor, cancellationToken);
    }

    /// <summary>
    /// Stop live watching and return the session to
    /// <see cref="JournalBrokerSessionState.Parked"/>, keeping the elevated process
    /// alive for a subsequent rescan or <see cref="StartWatchAsync"/>.
    /// No-op if already parked. Takes no cancellation token, mirroring
    /// <see cref="JournalBrokerClient.StopLiveWatchAsync"/>, which bounds itself with
    /// its own ack timeout.
    /// </summary>
    public async Task StopWatchAsync()
    {
        Task stopTask;
        lock (_stateLock)
        {
            EnsureOperableLocked();
            if (_state != JournalBrokerSessionState.Watching)
                return;
            if (_stopTask == null)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = completion.Task;
                _ = StopWatchCoreAsync(completion);
            }
            stopTask = _stopTask;
        }

        await stopTask.ConfigureAwait(false);
    }

    async Task StopWatchCoreAsync(TaskCompletionSource completion)
    {
        Exception? error = null;
        try
        {
            await _client.StopLiveWatchAsync().ConfigureAwait(false);

            // See StartWatchAsync: recheck under the lock so a Dispose or fault that
            // landed during the await above is never resurrected back to Parked. The
            // client-side watch is stopped either way, so the cached batch source is
            // always stale and is cleared regardless of which state wins.
            lock (_stateLock)
            {
                _batchSource = null;
                EnsureOperableLocked();
                _state = JournalBrokerSessionState.Parked;
            }
        }
        catch (Exception exception)
        {
            error = exception;
        }

        lock (_stateLock)
            _stopTask = null;

        if (error == null)
            completion.SetResult();
        else
            completion.SetException(error);
    }
}
