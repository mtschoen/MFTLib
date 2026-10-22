namespace MFTLib.Index;

/// <summary>
///     Owns one drive block's mapping and the readers inside it independently of the
///     <see cref="DriveBlock" /> object's lifetime, so index disposal can release a retired block
///     after garbage collection but before its finalizer runs. Released once: by the
///     <see cref="DriveBlock" /> finalizer when the last handle on a retired block becomes
///     unreachable, or by index disposal, which drains borrows first. A released block rejects
///     subsequent handle reads.
/// </summary>
sealed class DriveBlockRelease
{
    readonly BlockFile _block;

    /// <summary>
    ///     Completes once the release that started has unmapped the block. Continuations run
    ///     asynchronously so a waiter's remaining work never resumes on the finalizer thread that
    ///     completed this.
    /// </summary>
    readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Guards <see cref="_borrowCount" />, <see cref="_borrowsDrained" /> and the scheduled
    ///     delete.
    /// </summary>
    readonly Lock _gate = new();

    int _releaseState;

    int _borrowCount;

    /// <summary>
    ///     Created by the first asynchronous drain wait that finds borrows outstanding, and
    ///     completed when the last of them is returned. Null while nothing is waiting, so a
    ///     block that is never released asynchronously never allocates one.
    /// </summary>
    TaskCompletionSource? _borrowsDrained;

    string? _deleteAtPathOverride;
    Action<string>? _deleteDiagnostics;

    internal ReleaseHooks Hooks { get; }

    /// <summary>Per-instance test hooks for this release state.</summary>
    internal sealed class ReleaseHooks(DriveBlockRelease owner)
    {
        /// <summary>Invoked after borrows drain, before the block is unmapped, outside <see cref="_gate" />.</summary>
        internal Action? _releaseStarted;

        /// <summary>Invoked by <see cref="FileEntry.Children" /> once its borrow is counted, before it reads a row.</summary>
        internal Action? _readerAdmitted;

        /// <summary>How many readers are holding this block open right now, read under the owner's gate.</summary>
        internal int OutstandingBorrowCount
        {
            get
            {
                lock (owner._gate)
                {
                    return owner._borrowCount;
                }
            }
        }
    }

    internal DriveBlockRelease(BlockFile block)
    {
        _block = block;
        Hooks = new ReleaseHooks(this);
    }

    /// <summary>
    ///     True from the moment a release begins, before the block is unmapped, so a reader is
    ///     turned away rather than racing the unmap. Deliberately started rather than finished: a
    ///     reader must be refused for the whole of the release, while a caller that wants the
    ///     block actually closed asks <see cref="IsReleaseComplete" /> instead.
    /// </summary>
    internal bool IsReleaseStarted => Volatile.Read(ref _releaseState) != 0;

    /// <summary>
    ///     True once the release has unmapped the block. This, not <see cref="IsReleaseStarted" />,
    ///     is what says the block is no longer held: between the two, the mapping and its file are
    ///     still open.
    /// </summary>
    internal bool IsReleaseComplete => _completed.Task.IsCompleted;

    /// <summary>
    ///     Overrides the path deleted on release. A rescan renames the superseded block's file
    ///     aside before the replacement takes its canonical name, so the block's own
    ///     <see cref="BlockFile.Path" /> - fixed at construction and never updated by an external
    ///     rename - no longer names the file that must be removed once this block is done.
    /// </summary>
    internal void ScheduleDeleteAt(string path, Action<string>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_gate)
        {
            _deleteAtPathOverride = path;
            _deleteDiagnostics = diagnostics;
        }
    }

    /// <summary>
    ///     Cancels a previously scheduled override. Used when a rescan that renamed this block's
    ///     file aside then fails: the caller moves the file back to its original name and calls
    ///     this so a later release neither deletes the restored file nor, having already been
    ///     moved, silently no-ops on a path that no longer exists.
    /// </summary>
    internal void ClearScheduledDelete()
    {
        lock (_gate)
        {
            _deleteAtPathOverride = null;
            _deleteDiagnostics = null;
        }
    }

    /// <summary>
    ///     Records one reader, or reports that a release has begun and the block is on its way
    ///     out. Taken under the gate rather than through an interlocked increment so a borrow can
    ///     never be admitted after the release flag is read but before the count moves.
    /// </summary>
    internal bool TryTakeBorrow()
    {
        lock (_gate)
        {
            if (IsReleaseStarted)
            {
                return false;
            }

            _borrowCount++;
            return true;
        }
    }

    /// <summary>
    ///     Gives one reader's claim back, waking whichever release path is waiting for the last
    ///     of them.
    /// </summary>
    internal void ReturnBorrow()
    {
        lock (_gate)
        {
            _borrowCount--;
            if (_borrowCount > 0)
            {
                return;
            }

            _borrowsDrained?.TrySetResult();
        }
    }

    /// <summary>
    ///     Unmaps the block, or reports that another caller already began. Never waits, for
    ///     either a competing release or an outstanding borrow: the <see cref="DriveBlock" />
    ///     finalizer calls this, and a finalizer thread parked behind another thread stalls
    ///     finalization for the whole process. Waiting for borrows here would also be waiting for
    ///     nothing, because a borrow holds the <see cref="DriveBlock" /> itself and a borrowed
    ///     block is never collected.
    /// </summary>
    internal bool Release()
    {
        if (!TryBeginRelease())
        {
            return false;
        }

        CompleteRelease();
        return true;
    }

    /// <summary>
    ///     Claims the release for this caller, which also closes the door on new borrows. The
    ///     block stays mapped until <see cref="CompleteRelease" /> runs, so a deterministic
    ///     caller can drain the readers that are already inside in between.
    /// </summary>
    bool TryBeginRelease()
    {
        lock (_gate)
        {
            // Under the gate so the flag a borrow reads and the flag a release sets are the same
            // decision: a borrow admitted just before this point is counted and waited for, and
            // one that arrives just after is refused.
            return Interlocked.Exchange(ref _releaseState, 1) == 0;
        }
    }

    void CompleteRelease()
    {
        try
        {
            Hooks._releaseStarted?.Invoke();
            Unmap();
        }
        finally
        {
            // Completed in a finally rather than after the unmap. A waiter parked forever on a
            // completion that never arrives would be a new failure on top of a failed release.
            // The exception still reaches this call's own caller.
            _completed.TrySetResult();
        }
    }

    void Unmap()
    {
        string? deleteOverride;
        Action<string>? deleteDiagnostics;
        lock (_gate)
        {
            deleteOverride = _deleteAtPathOverride;
            deleteDiagnostics = _deleteDiagnostics;
        }

        _block.Dispose();
        if (deleteOverride is null)
        {
            return;
        }

        try
        {
            File.Delete(deleteOverride);
            deleteDiagnostics?.Invoke(
                $"Deleted block file '{deleteOverride}': superseded by a completed rescan and fully released.");
        }
        catch (IOException)
        {
            // The superseded block file is already unreferenced. A sharing violation here
            // leaves a stale file that the next open discards on serial or timestamp
            // mismatch, so it must not fail the rescan that triggered the release.
        }
        catch (UnauthorizedAccessException)
        {
            // Same reasoning as the IOException case above: a permission error on a file
            // nothing references any more must not fail the release that triggered it.
        }
    }

    /// <summary>
    ///     Releases, or waits out the release another caller already started, without blocking a
    ///     thread. Index disposal uses this, so it returns only once the block is actually
    ///     unmapped rather than once someone else has merely begun unmapping it, and only once
    ///     every reader that was inside the block has left it. Returns whether this call did the
    ///     releasing.
    /// </summary>
    internal async ValueTask<bool> ReleaseAsync()
    {
        if (!TryBeginRelease())
        {
            await _completed.Task.ConfigureAwait(false);
            return false;
        }

        await WaitForBorrowsToDrainAsync().ConfigureAwait(false);
        CompleteRelease();
        return true;
    }

    /// <summary>
    ///     Waits for the last borrow taken before the release began to come back, without holding
    ///     a thread while it does. Called only after <see cref="TryBeginRelease" /> has succeeded,
    ///     so the count only falls from here. The completion source is created here rather than up
    ///     front, so a block released with no reader inside it allocates nothing.
    /// </summary>
    Task WaitForBorrowsToDrainAsync()
    {
        lock (_gate)
        {
            if (_borrowCount == 0)
            {
                return Task.CompletedTask;
            }

            _borrowsDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _borrowsDrained.Task;
        }
    }
}
