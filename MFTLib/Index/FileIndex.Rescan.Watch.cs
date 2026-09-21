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
            var sessionToken = session.CallerToken;
            try
            {
                await StopWatchingAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }

            return new SuspendedWatch(RestartWholeSession: true, sessionToken);
        }

        if (session.ContainsTarget(driveLetter))
        {
            await session.Source.DisarmDriveAsync(driveLetter, cancellationToken).ConfigureAwait(false);
        }

        return new SuspendedWatch(RestartWholeSession: false, session.CallerToken);
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
            lock (_stateLock)
            {
                if (!TryGetDriveOrdinalLocked(driveLetter, out var driveOrdinal))
                {
                    return;
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
                    return;
                }
            }

            var target = BuildWatchTarget(driveLetter);
            session.RegisterTarget(target);
            WatchSessionFaults.Entry? previousFault;
            lock (_stateLock)
            {
                previousFault = session.Faults.TakeDrive(driveLetter);
                ClearWatchFailureLocked(driveLetter);
            }
            ArmWatchCatchUp(driveLetter);
            try
            {
                await session.Source.ArmDriveAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_stateLock)
                {
                    session.Faults.RestoreDrive(driveLetter, previousFault);
                }
                throw;
            }
            return;
        }

        ClearWatchFailure(driveLetter);
        RemoveStaleFaultedCatchUp(driveLetter);
        if (suspended.RestartWholeSession)
        {
            await StartWatchingCoreAsync(suspended.SessionToken, cancellationToken).ConfigureAwait(false);
        }
    }

    void ClearWatchFailure(char driveLetter)
    {
        lock (_stateLock)
        {
            ClearWatchFailureLocked(driveLetter);
        }
    }

    /// <summary>
    ///     What a rescan took off the watch, and what it therefore has to put back.
    ///     <see cref="ResumeDriveAfterRescanAsync" /> re-reads the live watch session instead of
    ///     carrying it here, so this holds only what a fresh read cannot recover: whether a session
    ///     existed at suspend time but had already ended without a stop, and the caller token to
    ///     restart it with.
    /// </summary>
    readonly record struct SuspendedWatch(bool RestartWholeSession, CancellationToken SessionToken);
}
