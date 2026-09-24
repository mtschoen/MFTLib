namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Not an <c>async</c> method, so its validation throws synchronously rather than through
    ///     the returned task; only the wait for the new session's readiness is asynchronous, and it
    ///     runs after <see cref="_stateLock" /> is released.
    ///     <paramref name="sessionToken" /> is what the session's lifetime is linked to and
    ///     <paramref name="startCancellationToken" /> only guards this call, which is how a rescan
    ///     restarts a whole session without reparenting the watch's lifetime to itself. Cancelling
    ///     it before the session is ready still abandons that session: a start nobody waits for
    ///     any more has nobody left to report its outcome to.
    ///     <paramref name="rescannedDriveLetter" /> is set only for that restart. It recovers one
    ///     drive, so every other drive still carrying a recorded watch failure is left out of the
    ///     new session with its message and faulted catch-up intact: its own failure, including a
    ///     live-watch <see cref="JournalCheckpointLoss" />, still stands and needs its own rescan.
    ///     The ended session's other faults were already retained in
    ///     <see cref="_unreportedWatchFaults" /> when the rescan reclaimed it. The public start
    ///     clears every armed drive's failure instead.
    /// </summary>
    Task StartWatchingCoreAsync(CancellationToken sessionToken, CancellationToken startCancellationToken,
        char? rescannedDriveLetter = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        startCancellationToken.ThrowIfCancellationRequested();

        var (targets, unresumableDrives) = BuildWatchTargets(rescannedDriveLetter);
        if (unresumableDrives.Count > 0)
        {
            lock (_stateLock)
            {
                foreach (var unresumable in unresumableDrives)
                {
                    RecordUnresumableCheckpointWatchFailureLocked(unresumable.DriveLetter, unresumable.DriveOrdinal);
                }
            }
        }

        if (targets.Count == 0)
        {
            return Task.CompletedTask;
        }

        var source = _options.WatchSource ?? throw new InvalidOperationException(
            $"{targets.Count} drive(s) support a live watch but " +
            $"{nameof(FileIndexOptions)}.{nameof(FileIndexOptions.WatchSource)} is not set.");

        WatchSession session;
        lock (_stateLock)
        {
            if (_watchSession is not null)
            {
                throw new InvalidOperationException("This index is already watching.");
            }

            ClearWatchFailures(targets);
            foreach (var target in targets)
            {
                ArmWatchCatchUpLocked(target.DriveLetter);
            }

            session = new WatchSession(
                CancellationTokenSource.CreateLinkedTokenSource(sessionToken), source, targets, sessionToken);
            session.Cancellation.Token.Register(() =>
            {
                lock (_stateLock)
                {
                    if (ReferenceEquals(_watchSession, session))
                    {
                        CancelPendingWatchCatchUpLocked();
                    }
                }
            });
            session.Pump = PumpAsync(session, targets);
            _watchSession = session;
        }

        return WaitForStartedSessionReadyAsync(session, startCancellationToken);
    }

    /// <summary>
    ///     The asynchronous half of a start: waits for <paramref name="session" /> to report ready,
    ///     and on any other outcome abandons it before rethrowing, so the failure is reported to
    ///     this caller and the index is left free to start again.
    /// </summary>
    async Task WaitForStartedSessionReadyAsync(WatchSession session, CancellationToken startCancellationToken)
    {
        try
        {
            await session.Ready.WaitAsync(startCancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbandonUnreadyWatchSessionAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Cancels and releases a session that never became ready, once its pump has finished, the
    ///     way <see cref="StopWatchingAsync" /> releases one but without rethrowing its faults: the
    ///     start abandoning it reports the failure itself. A session a stop, a disposal, or its own
    ///     pump already released is left alone.
    /// </summary>
    async Task AbandonUnreadyWatchSessionAsync(WatchSession session)
    {
        if (!IsCurrentWatchSession(session))
        {
            return;
        }

        try
        {
            await session.Cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (!IsCurrentWatchSession(session))
        {
            // Released, and its cancellation disposed, between the check above and the cancel.
            return;
        }

        await session.Pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        bool released;
        lock (_stateLock)
        {
            released = ReferenceEquals(_watchSession, session);
            if (released)
            {
                _watchSession = null;
                ResetWatchCatchUpLocked();
            }
        }

        if (released)
        {
            session.Cancellation.Dispose();
        }
    }
}
