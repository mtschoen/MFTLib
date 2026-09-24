namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Puts the drive back on the watch after a swap that failed, so the caller's exception is
    ///     the only consequence.
    /// </summary>
    async Task ResumeAfterFailedSwapAsync(char driveLetter, SuspendedWatch suspended,
        Exception swapFailure, CancellationToken cancellationToken)
    {
        try
        {
            await ResumeDriveAfterRescanAsync(driveLetter, suspended, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception resumeFailure)
        {
            RecordWatchFailure(driveLetter, swapFailure);
            throw new AggregateException(
                $"Drive {driveLetter} could not be re-armed after its rescan failed, so its watch is stopped.", swapFailure, resumeFailure);
        }
    }

    /// <summary>
    ///     Stops the rescanned drive at the watch source before the gate is taken.
    /// </summary>
    /// <remarks>
    ///     A session whose pump has already completed is reclaimed here and restarted once the scan
    ///     finishes. A disarm that fails on a session that has ended by the time the failure is
    ///     examined (see <see cref="HasSessionEndedAfterRejectionAsync" />) is the same case reached
    ///     one step later: the stream it would have stopped is already gone, so the failure becomes
    ///     a restart instead of a rescan failure. A disarm that fails on a session still running is
    ///     this drive's own failure and propagates.
    /// </remarks>
    async Task<SuspendedWatch> SuspendDriveForRescanAsync(char driveLetter, CancellationToken cancellationToken)
    {
        WatchSession? session;
        lock (_stateLock)
        {
            session = _watchSession;
        }

        if (session is null)
        {
            return default;
        }

        if (session.Pump.IsCompleted)
        {
            var restart = EndedWithoutCancellation(session);
            await ReclaimEndedWatchSessionAsync(session, driveLetter, cancellationToken).ConfigureAwait(false);
            return new SuspendedWatch(session, restart);
        }

        if (session.ContainsTarget(driveLetter))
        {
            try
            {
                await session.Source.DisarmDriveAsync(driveLetter, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception rejection) when (!cancellationToken.IsCancellationRequested)
            {
                if (!await HasSessionEndedAfterRejectionAsync(session, rejection, cancellationToken)
                        .ConfigureAwait(false))
                {
                    throw;
                }

                return new SuspendedWatch(session, EndedWithoutCancellation(session));
            }
        }

        return new SuspendedWatch(session, RestartWholeSession: false);
    }

    /// <summary>
    ///     Puts the rescanned drive back on the watch it was taken off.
    /// </summary>
    /// <remarks>
    ///     Reads <see cref="_watchSession" /> fresh here rather than trusting whatever
    ///     <see cref="SuspendDriveForRescanAsync" /> saw: that was a snapshot taken before the scan
    ///     ran, and the scan is awaited without holding <see cref="_rescanGate" /> released or
    ///     <see cref="_stateLock" /> held, so a session can start (or, for the drive this rescan
    ///     disarmed, keep running) while the scan is still in flight. A drive excluded from that
    ///     snapshot because it had no watchable block yet, or because it was still marked
    ///     unresumable, must still join a session that exists by the time the scan finishes, not
    ///     be silently left off one the stale snapshot never saw (PR 230 review finding 2).
    ///     <para>
    ///         The current session is re-armed only while it is still running. A session that has
    ///         ended (its pump decided to stop reading because every watched drive failed, its pump
    ///         completed, or it was cancelled) has no stream to arm onto, so it is reclaimed and,
    ///         unless it ended through cancellation, a fresh session is started in its place. The
    ///         same happens when the arm itself fails and the session is found to have ended by
    ///         then. A running session is never stopped to make room for the restart.
    ///     </para>
    ///     <paramref name="suspended" /> remains the answer for the one case a fresh read cannot
    ///     recover: no session at all exists right now, but one did at suspend time and had
    ///     already ended without a stop (<see cref="SuspendedWatch.RestartWholeSession" />).
    /// </remarks>
    async Task ResumeDriveAfterRescanAsync(char driveLetter, SuspendedWatch suspended,
        CancellationToken cancellationToken)
    {
        WatchSession? currentSession;
        lock (_stateLock)
        {
            currentSession = _watchSession;
        }

        if (currentSession is { } session)
        {
            if (await TryReArmOnRunningSessionAsync(driveLetter, session, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var restartFrom = EndedWithoutCancellation(session) ? session : null;
            await ReclaimEndedWatchSessionAsync(session, driveLetter, cancellationToken).ConfigureAwait(false);
            await ClearAndRestartAfterRescanAsync(driveLetter, restartFrom, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ClearAndRestartAfterRescanAsync(driveLetter,
            suspended.RestartWholeSession ? suspended.Session : null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Registers and arms the rescanned drive on <paramref name="session" />, returning false
    ///     without arming anything when the session has ended. The ended check, the target
    ///     registration, and the failure clear share one <see cref="_stateLock" /> section, the same
    ///     lock the pump holds while it decides whether any watched drive remains, so either the
    ///     pump sees this drive as watched again and keeps reading, or this sees the session marked
    ///     ended. An arm that fails once the session has ended also returns false; any other arm
    ///     failure restores the drive's earlier session fault and propagates.
    /// </summary>
    async Task<bool> TryReArmOnRunningSessionAsync(char driveLetter, WatchSession session,
        CancellationToken cancellationToken)
    {
        IndexWatchTarget target;
        WatchSessionFaults.Entry? previousFault;
        lock (_stateLock)
        {
            if (HasWatchSessionEnded(session))
            {
                return false;
            }

            if (!TryGetDriveOrdinalLocked(driveLetter, out var driveOrdinal))
            {
                return true;
            }

            if (_cacheOnlyUnresumableCheckpointOrdinals.Contains(driveOrdinal))
            {
                // The scan did not replace the block (it failed without throwing, or was
                // never run because the caller decided not to), so the block's cursor is
                // still the one the journal cannot resume. Registering or arming it onto the
                // session would resume from that cursor; leave the drive's existing refusal
                // (WatchFailureMessage, WatchCatchUpState.Faulted) exactly as it was instead
                // of arming it or clearing the message that explains why it is not watched
                // (PR 230 review finding 1).
                return true;
            }

            target = BuildWatchTarget(driveLetter);
            session.RegisterTarget(target);
            previousFault = session.Faults.TakeDrive(driveLetter);
            ClearWatchFailureLocked(driveLetter);
            ArmWatchCatchUpLocked(driveLetter);
        }

        try
        {
            await session.Source.ArmDriveAsync(target, cancellationToken).ConfigureAwait(false);
            lock (_stateLock)
            {
                _unreportedWatchFaults.TakeDrive(driveLetter);
            }

            return true;
        }
        catch (Exception rejection)
        {
            lock (_stateLock)
            {
                session.Faults.RestoreDrive(driveLetter, previousFault);
            }

            if (await HasSessionEndedAfterRejectionAsync(session, rejection, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            throw;
        }
    }

    /// <summary>
    ///     Decides whether a source's rejection of an arm or disarm on <paramref name="session" />
    ///     means the session has ended. A source releases its stream before the pump reaches its
    ///     end-of-stream bookkeeping, so the session can still read as running when the rejection
    ///     arrives. A <see cref="WatchStreamNotRunningException" /> after the session's stream has
    ///     started (<see cref="WatchSession.StreamStarted" />) can only mean that stream was
    ///     released, so this waits for the pump to finish, bounded by
    ///     <paramref name="cancellationToken" />, before reading the session's state. Any other
    ///     rejection, or one before the stream started, is judged on the session's state as it
    ///     stands, because waiting on a pump that is still reading would never end.
    /// </summary>
    async Task<bool> HasSessionEndedAfterRejectionAsync(WatchSession session, Exception rejection,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (rejection is WatchStreamNotRunningException && session.StreamStarted)
        {
            await session.Pump.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return HasWatchSessionEnded(session);
    }

    /// <summary>
    ///     Clears the rescanned drive's watch failure, stale faulted catch-up, and retained fault,
    ///     then, when <paramref name="restartFrom" /> names the ended session to replace, starts a
    ///     fresh session linked to that session's caller token. The fresh session leaves out every
    ///     other drive still carrying a recorded watch failure.
    /// </summary>
    Task ClearAndRestartAfterRescanAsync(char driveLetter, WatchSession? restartFrom,
        CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            ClearWatchFailureLocked(driveLetter);
            _unreportedWatchFaults.TakeDrive(driveLetter);
        }

        RemoveStaleFaultedCatchUp(driveLetter);
        return restartFrom is null
            ? Task.CompletedTask
            : StartWatchingCoreAsync(restartFrom.CallerToken, cancellationToken, rescannedDriveLetter: driveLetter);
    }

    /// <summary>
    ///     Reclaims a session that ended without a stop, the way <see cref="StopWatchingAsync" />
    ///     does, with two differences that keep a rescan's recovery confined to its own drive: a
    ///     drive whose watch failure is still recorded keeps its
    ///     <see cref="WatchCatchUpState.Faulted" /> catch-up slot, and the session's outstanding
    ///     faults, other than <paramref name="recoveredDriveLetter" />'s, are not rethrown here but
    ///     retained in <see cref="_unreportedWatchFaults" /> for the next stop, whether or not a
    ///     fresh session follows and however that session later ends. A session a stop already
    ///     released was reported by that stop, so nothing is retained from it. The session is not
    ///     cancelled here: its pump has already decided to stop, so this only waits for it.
    /// </summary>
    async Task ReclaimEndedWatchSessionAsync(WatchSession session, char recoveredDriveLetter,
        CancellationToken cancellationToken)
    {
        await session.Pump.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            if (ReferenceEquals(_watchSession, session))
            {
                _watchSession = null;
                ResetWatchCatchUpKeepingRecordedFailuresLocked();
                _unreportedWatchFaults.RetainFrom(session.Faults, recoveredDriveLetter);
            }
        }

        session.Cancellation.Dispose();
    }

    /// <summary>
    ///     Whether <paramref name="session" /> can no longer carry a re-armed drive: its pump has
    ///     decided to stop reading (<see cref="WatchSession.Ended" />), its pump has completed, or
    ///     its cancellation was requested.
    /// </summary>
    bool HasWatchSessionEnded(WatchSession session)
    {
        lock (_stateLock)
        {
            return session.Ended || session.Pump.IsCompleted || session.Cancellation.IsCancellationRequested;
        }
    }

    /// <summary>
    ///     A session cancelled by a stop or by its caller's token was ended on purpose, so a rescan
    ///     reclaims it but does not start another one in its place.
    /// </summary>
    static bool EndedWithoutCancellation(WatchSession session)
    {
        return !session.Cancellation.IsCancellationRequested;
    }

    /// <summary>
    ///     What a rescan took off the watch, and what it therefore has to put back.
    ///     <see cref="Session" /> is the session the rescan found at suspend time and disarmed on,
    ///     if it disarmed at all. <see cref="ResumeDriveAfterRescanAsync" /> re-reads the live watch
    ///     session instead of trusting it, so the carried session supplies only what a fresh read
    ///     cannot recover once no session is current: whether it had already ended without a stop
    ///     by the time the rescan saw it, and the caller token to restart with.
    /// </summary>
    readonly record struct SuspendedWatch(WatchSession? Session, bool RestartWholeSession);
}
