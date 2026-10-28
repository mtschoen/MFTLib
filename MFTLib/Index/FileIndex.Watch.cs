namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Starts the root's watch from its block's journal cursor, returning when the channel
    ///     is connected and the start request is written. An already running watch remains running.
    ///     A source without a watch or a root without an MFT-backed block is NotApplicable and
    ///     records no request. A refused or failed start is Failed and retains the watch request
    ///     for rescan recovery, except when the caller cancels it.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">The call was made from inside a Changed, WatchFaulted or WatchStateChanged handler of any index.</exception>
    public Task<RootOperationResult> StartWatchingAsync(CancellationToken cancellationToken)
    {
        if (RejectedTask<RootOperationResult>(nameof(StartWatchingAsync)) is { } rejected)
        {
            return rejected;
        }

        return RunLifecycleOperationAsync(HasWatchableBlock,
            () => _view.Scan.StartRootWatchingAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     Stops the root's watch and drains its teardown. An outstanding watch or subscriber
    ///     fault is returned once as Failed. A root with no requested, current or retiring watch
    ///     is NotApplicable. Stop wins over a concurrent rescan or recovery and prevents its restart.
    ///     Cancellation bounds the teardown wait, while teardown continues independently.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">The call was made from inside a Changed, WatchFaulted or WatchStateChanged handler of any index.</exception>
    public Task<RootOperationResult> StopWatchingAsync(CancellationToken cancellationToken)
    {
        if (RejectedTask<RootOperationResult>(nameof(StopWatchingAsync)) is { } rejected)
        {
            return rejected;
        }

        return RunLifecycleOperationAsync(IsWatching,
            () => _view.Scan.StopRootWatchingAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     Rebuilds the root and publishes its replacement block. Failed production
    ///     retains its block and healthy watch and is reported as Failed. Publication retires the
    ///     old watch and restarts it only while requested. A replacement watch start failure raises
    ///     RescanRestart while the successful scan is reported as Succeeded. Exhausted catch-up
    ///     recovery returns Failed with JournalCatchUpLostException and keeps the last block.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">The call was made from inside a Changed, WatchFaulted or WatchStateChanged handler of any index.</exception>
    public Task<RootOperationResult> RescanAsync(CancellationToken cancellationToken)
    {
        if (RejectedTask<RootOperationResult>(nameof(RescanAsync)) is { } rejected)
        {
            return rejected;
        }

        return RunLifecycleOperationAsync(applicable: null,
            () => _view.Scan.RescanRootAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     Follows the root's current catch-up instance, or its pending replacement. A faulted
    ///     or superseded instance is Failed; an unwatched root is NotApplicable. A healthy watch
    ///     survives failed rescan production; replacement publication retires its wait. Caller
    ///     cancellation and index disposal cancel this wait without stopping the watch.
    ///     An unsettled wait rejects callback reentrancy; settled waits remain readable.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">A wait that is not yet settled was requested from inside a Changed, WatchFaulted or WatchStateChanged handler of any index.</exception>
    public Task<RootOperationResult> WaitForCatchUpAsync(CancellationToken cancellationToken)
    {
        Task? wait;
        CancellationToken disposalToken;
        try
        {
            ThrowIfDisposed();
            lock (StateLock)
            {
                wait = _view.Scan.GetCatchUpWaitLocked();
            }

            disposalToken = DisposalToken;
        }
        catch (ObjectDisposedException disposal) when (IsInsideHandler())
        {
            return Task.FromException<RootOperationResult>(
                RejectInsideHandler(nameof(WaitForCatchUpAsync)) ?? disposal);
        }

        if (!cancellationToken.IsCancellationRequested && wait is { IsCompleted: false } &&
            RejectedTask<RootOperationResult>(nameof(WaitForCatchUpAsync)) is { } rejected)
        {
            return rejected;
        }

        return new CatchUpWait(_view.Scan.Root, wait, cancellationToken, disposalToken).Completion;
    }

    bool HasWatchableBlock()
    {
        lock (StateLock)
        {
            return _view.Scan.HasWatchableBlockLocked();
        }
    }

    bool IsWatching()
    {
        lock (StateLock)
        {
            return _view.Scan.IsWatchingLocked();
        }
    }

    async Task<RootOperationResult> RunLifecycleOperationAsync(Func<bool>? applicable,
        Func<Task> operation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        RootOperationResult result;
        try
        {
            if (applicable is not null && !applicable())
            {
                result = new RootOperationResult(_view.Scan.Root, RootOperationOutcome.NotApplicable, null);
            }
            else
            {
                await operation().ConfigureAwait(false);
                result = new RootOperationResult(_view.Scan.Root, RootOperationOutcome.Succeeded, null);
            }
        }
        catch (Exception failure)
        {
            result = new RootOperationResult(_view.Scan.Root, RootOperationOutcome.Failed, failure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    // No caller token: a direct apply has no stop of its own, and the index's disposal token would
    // let disposal turn a batch already queued for the gate into a cancellation instead of the
    // ObjectDisposedException a disposed index reports.
    internal Task<IReadOnlyList<FileChange>> ApplyJournalEntriesAsync(VolumeKey volume,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn) =>
        ApplyJournalEntriesAsync(_view.Scan.ServesVolume(volume) ? _view.Scan.Root : volume, entries, journalIdentifier,
            nextUsn);

    internal async Task<IReadOnlyList<FileChange>> ApplyJournalEntriesAsync(RootKey root,
        IReadOnlyList<UsnJournalEntry> entries, ulong journalIdentifier, long nextUsn)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entries);
        var runtime = GetVolumeScan(root);
        var changes = await runtime.ApplyJournalEntriesCoreAsync(null, entries, journalIdentifier, nextUsn,
            CancellationToken.None).ConfigureAwait(false);
        runtime.RaiseChanged(changes);
        return changes;
    }
}
