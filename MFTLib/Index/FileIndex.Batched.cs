namespace MFTLib.Index;

/// <summary>
///     Lifecycle operations run requested drives concurrently and return one result per key in
///     request order. Caller errors and cancellation fail the whole call. Each sequence is
///     materialized and validated before any operation starts; duplicate and unconfigured keys
///     and the default key are rejected. An empty sequence returns an empty list unless cancelled.
/// </summary>
public sealed partial class FileIndex
{
    /// <summary>
    ///     Starts each drive's watch from its block's journal cursor, returning when the channel
    ///     is connected and StartWatch is written. Already running watches remain running.
    ///     A source without a watch or a drive without an MFT-backed block is NotApplicable and
    ///     records no request. A refused or failed start is Failed and retains the watch request
    ///     for rescan recovery, except when the caller cancels it.
    /// </summary>
    public Task<IReadOnlyList<DriveOperationResult>> StartWatchingAsync(IEnumerable<DriveKey> drives,
        CancellationToken cancellationToken)
    {
        if (RejectedTask<IReadOnlyList<DriveOperationResult>>(nameof(StartWatchingAsync)) is { } rejected)
        {
            return rejected;
        }

        return RunBatchAsync(drives, HasWatchableBlock,
            key => StartDriveWatchingAsync(key, cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     Stops each requested watch and drains its teardown. An outstanding watch or subscriber
    ///     fault is returned once as Failed. A drive with no requested, current or retiring watch
    ///     is NotApplicable. Stop wins over a concurrent rescan or recovery and prevents its restart.
    ///     Cancellation bounds the teardown wait, while teardown continues independently.
    /// </summary>
    public Task<IReadOnlyList<DriveOperationResult>> StopWatchingAsync(IEnumerable<DriveKey> drives,
        CancellationToken cancellationToken)
    {
        if (RejectedTask<IReadOnlyList<DriveOperationResult>>(nameof(StopWatchingAsync)) is { } rejected)
        {
            return rejected;
        }

        return RunBatchAsync(drives, IsWatching,
            key => StopDriveWatchingAsync(key, cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     Rebuilds each requested drive and publishes its replacement snapshot. Failed production
    ///     retains its block and healthy watch and is reported as Failed. Publication retires the
    ///     old watch and restarts it only while requested. A replacement watch start failure raises
    ///     RescanRestart while the successful scan is reported as Succeeded. Exhausted catch-up
    ///     recovery returns Failed with JournalCatchUpLostException and keeps the last block.
    /// </summary>
    public Task<IReadOnlyList<DriveOperationResult>> RescanAsync(IEnumerable<DriveKey> drives,
        CancellationToken cancellationToken)
    {
        if (RejectedTask<IReadOnlyList<DriveOperationResult>>(nameof(RescanAsync)) is { } rejected)
        {
            return rejected;
        }

        return RunBatchAsync(drives, applicable: null,
            key => RescanDriveAsync(key, cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     Follows each drive's current catch-up instance, or its pending replacement. A faulted
    ///     or superseded instance is Failed; an unwatched drive is NotApplicable. A healthy watch
    ///     survives failed rescan production; replacement publication retires its wait. Caller
    ///     cancellation and index disposal cancel this wait without stopping the watches.
    ///     An unsettled wait rejects callback reentrancy; settled waits remain readable.
    /// </summary>
    public Task<IReadOnlyList<DriveOperationResult>> WaitForCatchUpAsync(IEnumerable<DriveKey> drives,
        CancellationToken cancellationToken)
    {
        try
        {
            var keys = ValidateBatch(drives);
            Task?[] waits;
            lock (_stateLock)
            {
                waits = [.. keys.Select(key => GetCatchUpWaitLocked(GetDriveRuntime(key)))];
            }

            if (!cancellationToken.IsCancellationRequested && waits.Any(wait => wait is { IsCompleted: false }) &&
                RejectedTask<IReadOnlyList<DriveOperationResult>>(nameof(WaitForCatchUpAsync)) is { } rejected)
            {
                return rejected;
            }

            return new BatchedCatchUpWait(keys, waits, cancellationToken, DisposalToken).Completion;
        }
        catch (ObjectDisposedException disposal) when (IsInsideHandlerOfThisIndex())
        {
            return Task.FromException<IReadOnlyList<DriveOperationResult>>(
                RejectInsideHandler(nameof(WaitForCatchUpAsync)) ?? disposal);
        }
    }

    bool HasWatchableBlock(DriveKey drive)
    {
        lock (_stateLock)
        {
            return GetDriveRuntime(drive).HasWatchableBlockLocked();
        }
    }

    bool IsWatching(DriveKey drive)
    {
        lock (_stateLock)
        {
            return GetDriveRuntime(drive).IsWatchingLocked();
        }
    }

    async Task<IReadOnlyList<DriveOperationResult>> RunBatchAsync(IEnumerable<DriveKey> drives,
        Func<DriveKey, bool>? applicable, Func<DriveKey, Task> operation, CancellationToken cancellationToken)
    {
        var keys = ValidateBatch(drives);
        var results = await Task.WhenAll(keys.Select(key => RunOneAsync(key, applicable, operation)))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    DriveKey[] ValidateBatch(IEnumerable<DriveKey> drives)
    {
        ArgumentNullException.ThrowIfNull(drives);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var keys = drives.ToArray();
        HashSet<DriveKey> seen = [];
        foreach (var key in keys)
        {
            if (!_driveRuntimes.ContainsKey(key.Validated()))
            {
                throw new ArgumentException($"Drive {key} is not part of this index.", nameof(drives));
            }

            if (!seen.Add(key))
            {
                throw new ArgumentException($"Drive {key} is listed more than once.", nameof(drives));
            }
        }

        return keys;
    }

    static async Task<DriveOperationResult> RunOneAsync(DriveKey drive, Func<DriveKey, bool>? applicable,
        Func<DriveKey, Task> operation)
    {
        try
        {
            if (applicable is not null && !applicable(drive))
            {
                return new DriveOperationResult(drive, DriveOperationOutcome.NotApplicable, null);
            }

            await operation(drive).ConfigureAwait(false);
            return new DriveOperationResult(drive, DriveOperationOutcome.Succeeded, null);
        }
        catch (Exception failure)
        {
            return new DriveOperationResult(drive, DriveOperationOutcome.Failed, failure);
        }
    }
}
