namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     One watch session's cancellation, pump, and source held together, so a start racing a
    ///     stop cannot claim one and publish another. A single
    ///     <see cref="FileIndex._watchSession" /> field is claimed, read, and cleared under
    ///     <see cref="FileIndex._stateLock" />.
    /// </summary>
    sealed class WatchSession(CancellationTokenSource cancellation, IIndexWatchSource source,
        CancellationToken callerToken)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        // The exact source the pump is reading, so a rescan reaches the arm and disarm
        // operations of the stream in flight rather than of some other instance.
        public IIndexWatchSource Source { get; } = source;

        /// <summary>The token this session's source was linked to, which a restart relinks to.</summary>
        public CancellationToken CallerToken { get; } = callerToken;

        public Task<Exception?> Pump { get; set; } = Task.FromResult<Exception?>(null);
    }

    async Task<Exception?> PumpAsync(WatchSession session, IReadOnlyList<IndexWatchTarget> targets)
    {
        // Read before the yield, which is to say inside StartWatchingCoreAsync's lock and while
        // the session is provably still this index's own, so nothing here reads a token source a
        // stop has already disposed.
        var source = session.Source;
        var cancellationToken = session.Cancellation.Token;

        // Returning to StartWatchingAsync before any source code runs is what makes it safe to
        // launch this pump inside _stateLock: the lock is released before the source connects.
        await Task.Yield();

        // The one drop the ordinal dictionary cannot record. A letter TryGetDriveOrdinal does not
        // resolve has no key, so without this a second failure item for it would be announced
        // twice. Session-local on purpose: a fresh session starts with nothing dropped, which is
        // the same rule arming follows for every drive that does have an ordinal.
        var droppedDriveLettersWithoutOrdinal = new HashSet<char>();

        Exception? firstFault = null;
        var dropped = false;
        try
        {
            await foreach (var item in source.StartWatching(targets, cancellationToken).ConfigureAwait(false))
            {
                if (item is DriveWatchFailure failure)
                {
                    if (DropDrive(failure.DriveLetter, failure.Exception, WatchFaultKind.Source,
                            droppedDriveLettersWithoutOrdinal))
                    {
                        firstFault ??= failure.Exception;
                        dropped = true;
                    }
                }
                else if (item is JournalBatch batch &&
                         !IsDriveWatchFaulted(batch.DriveLetter, droppedDriveLettersWithoutOrdinal))
                {
                    dropped = !TryApplyBatch(batch, droppedDriveLettersWithoutOrdinal,
                        cancellationToken, ref firstFault);
                }

                if (dropped && !AnyWatchedDriveRemains(targets, droppedDriveLettersWithoutOrdinal))
                {
                    // Every watched drive has failed. Breaking disposes the enumerator, which ends
                    // the watch at the source; the session stays so StopWatchingAsync still rethrows.
                    break;
                }

                dropped = false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (firstFault is null)
            {
                throw;
            }

            // Preserve a fault already observed before an ordinary stop cancelled the source.
        }
        catch (Exception exception)
        {
            firstFault ??= exception;
            RaiseWatchFaulted(new WatchFault(WatchFaultKind.Source, null, exception));
        }

        if (firstFault is null && !cancellationToken.IsCancellationRequested &&
            AnyWatchedDriveRemains(targets, droppedDriveLettersWithoutOrdinal))
        {
            ReportSourceEndedWithoutStop(session, targets, droppedDriveLettersWithoutOrdinal);
        }

        return firstFault;
    }

    /// <summary>
    ///     Answers a stream that ended while drives were still being watched, which no stop asked
    ///     for. Nothing is watching afterwards, so every drive still on the watch is marked with
    ///     the reason and the end is announced once, with no drive letter, because it belongs to
    ///     the session rather than to any one drive. The session claim goes with it, so a consumer
    ///     recovers by starting a fresh session rather than by stopping one that is already over.
    ///     A stream that ends after every watched drive has already faulted is a different case
    ///     and is left alone: those drives carry their own messages, their faults were announced
    ///     when they were recorded, and the session stays claimed so
    ///     <see cref="StopWatchingAsync" /> still rethrows the first of them.
    /// </summary>
    void ReportSourceEndedWithoutStop(WatchSession session, IReadOnlyList<IndexWatchTarget> targets,
        HashSet<char> droppedDriveLettersWithoutOrdinal)
    {
        var sourceEnded = new InvalidOperationException(
            "The watch source ended its stream without being stopped, so no drive is being watched.");
        lock (_stateLock)
        {
            foreach (var target in targets)
            {
                if (TryGetDriveOrdinalLocked(target.DriveLetter, out var driveOrdinal))
                {
                    _watchFailureMessagesByOrdinal.TryAdd(driveOrdinal, sourceEnded.Message);
                }
                else
                {
                    droppedDriveLettersWithoutOrdinal.Add(char.ToUpperInvariant(target.DriveLetter));
                }
            }

            if (ReferenceEquals(_watchSession, session))
            {
                _watchSession = null;
            }
        }

        // Disposed here because this is the last thing that holds the session: a stop racing this
        // release finds the field already cleared and returns without touching the source.
        session.Cancellation.Dispose();
        RaiseWatchFaulted(new WatchFault(WatchFaultKind.Source, null, sourceEnded));
    }

    /// <summary>
    ///     Applies one batch and raises <see cref="Changed" /> under separate catches, which is
    ///     what makes an apply failure distinguishable from a subscriber failure. Returns false
    ///     only when the apply failed and the drive was therefore dropped. Both catches carry the
    ///     filter that keeps this session's own cancellation out of them: such a cancellation
    ///     escapes to <see cref="PumpAsync" />'s handler, where an ordinary stop is not a fault
    ///     and drops no drive.
    /// </summary>
    bool TryApplyBatch(JournalBatch batch, HashSet<char> droppedDriveLettersWithoutOrdinal,
        CancellationToken cancellationToken, ref Exception? firstFault)
    {
        IReadOnlyList<FileChange> changes;
        try
        {
            changes = ApplyJournalEntriesCore(batch.DriveLetter, batch.Entries, batch.JournalId, batch.NextUsn);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            firstFault ??= exception;
            DropDrive(batch.DriveLetter, exception, WatchFaultKind.Apply, droppedDriveLettersWithoutOrdinal);
            return false;
        }

        try
        {
            RaiseChanged(changes);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (firstFault is null)
            {
                firstFault = exception;

                // The mutation and the cursor are already durable when a subscriber runs, so a
                // broken handler is not this drive's problem and does not drop it.
                RaiseWatchFaulted(new WatchFault(WatchFaultKind.Subscriber, batch.DriveLetter, exception));
            }
        }

        return true;
    }

    /// <summary>
    ///     Records one drive's watch failure and announces it, returning false when that drive was
    ///     already dropped so a second failure for it changes nothing.
    /// </summary>
    bool DropDrive(char driveLetter, Exception exception, WatchFaultKind kind,
        HashSet<char> droppedDriveLettersWithoutOrdinal)
    {
        bool firstDrop;
        lock (_stateLock)
        {
            firstDrop = TryGetDriveOrdinalLocked(driveLetter, out var driveOrdinal)
                ? _watchFailureMessagesByOrdinal.TryAdd(driveOrdinal, exception.Message)
                : droppedDriveLettersWithoutOrdinal.Add(char.ToUpperInvariant(driveLetter));
        }

        if (!firstDrop)
        {
            return false;
        }

        RaiseWatchFaulted(new WatchFault(kind, driveLetter, exception));
        return true;
    }

    bool IsDriveWatchFaulted(char driveLetter, HashSet<char> droppedDriveLettersWithoutOrdinal)
    {
        lock (_stateLock)
        {
            return TryGetDriveOrdinalLocked(driveLetter, out var driveOrdinal)
                ? _watchFailureMessagesByOrdinal.ContainsKey(driveOrdinal)
                : droppedDriveLettersWithoutOrdinal.Contains(char.ToUpperInvariant(driveLetter));
        }
    }

    /// <summary>
    ///     Records and announces one drive's watch failure from outside the pump, for a rescan that
    ///     could not put the drive back on its watch. A drive with no ordinal has nowhere to record
    ///     the message, so the announcement is the whole signal, which is why the throwaway set is
    ///     all this needs where the pump keeps a session-local one.
    /// </summary>
    void RecordWatchFailure(char driveLetter, Exception exception)
    {
        DropDrive(driveLetter, exception, WatchFaultKind.Source, []);
    }

    /// <summary>
    ///     Recomputed from the failure records each time rather than counted down, so a drive a
    ///     rescan re-armed and cleared counts as live again with no second structure to keep in
    ///     step.
    /// </summary>
    bool AnyWatchedDriveRemains(IReadOnlyList<IndexWatchTarget> targets,
        HashSet<char> droppedDriveLettersWithoutOrdinal)
    {
        foreach (var target in targets)
        {
            if (!IsDriveWatchFaulted(target.DriveLetter, droppedDriveLettersWithoutOrdinal))
            {
                return true;
            }
        }

        return false;
    }

    void RaiseWatchFaulted(WatchFault fault)
    {
        try
        {
            WatchFaulted?.Invoke(fault);
        }
        catch (Exception exception)
        {
            // A fault reporter that throws cannot safely report its own failure. The original
            // fault remains available through StopWatchingAsync. Discarded through the variable
            // rather than an empty body, which is this repository's idiom for a deliberate
            // swallow and what keeps RCS1075 honest here.
            _ = exception;
        }
    }
}
