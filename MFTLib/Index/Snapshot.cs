using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     The set of drive blocks current at one moment. A <see cref="FileEntry" /> holds a
///     reference to its snapshot, keeping a block retired by a rescan mapped while that handle
///     remains reachable. The finalizer releases a retired snapshot once its handles become
///     unreachable while the index lives. <see cref="ReleaseNowAsync" /> is the internal
///     deterministic release path, used by index disposal and by tests that tear a snapshot down
///     at a point they choose; released snapshots reject subsequent handle reads.
/// </summary>
public sealed class Snapshot
{
    readonly SnapshotRelease _release;

    Snapshot(SnapshotRelease release)
    {
        _release = release;
    }

    /// <summary>
    ///     True from the moment a release begins, before the first block is unmapped, so a
    ///     reader is turned away rather than racing the unmap. Set by <see cref="SnapshotRelease.Release" />
    ///     on both the <see cref="ReleaseNowAsync" /> path and the finalizer path. Deliberately started
    ///     rather than finished: a reader must be refused for the whole of the release, while a
    ///     caller that wants the blocks actually closed asks
    ///     <see cref="SnapshotRelease.IsReleaseComplete" /> instead.
    /// </summary>
    internal bool IsReleased => _release.IsReleaseStarted;

    internal SnapshotRelease ReleaseState => _release;

    /// <summary>
    ///     Releases the snapshot's blocks for a caller who simply dropped every handle. The catch
    ///     is deliberately unconditional and local to this path: releasing unmaps a view and can
    ///     delete a retired block file, so an unbalanced count or a file the operating system will
    ///     not unlink surfaces as an exception, and an exception leaving a finalizer terminates the
    ///     process with a stack no consumer can act on. The worst case swallowed here is a leaked
    ///     mapping or a leftover file, which is what the caller already has.
    ///     <see cref="ReleaseNowAsync" /> keeps propagating, because a deterministic teardown
    ///     failure is worth surfacing.
    /// </summary>
    [SuppressMessage("Roslynator", "RCS1075",
        Justification = "A finalizer is the one place where catching everything and doing nothing is the " +
                        "correct behaviour: an exception that escapes it terminates the process. There is " +
                        "nothing to log to and nothing to retry, and the swallowed worst case is a leaked " +
                        "mapping, which is strictly better than the alternative. Scoped to this method only.")]
    ~Snapshot()
    {
        try
        {
            _release.Release();
        }
        catch (Exception)
        {
            // See this finalizer's summary.
        }
    }

    public IReadOnlyList<DriveBlock> DriveBlocks => _release.DriveBlocks;

    public int DriveCount => _release.DriveBlocks.Length;

    /// <summary>
    ///     Takes one reference on every block. Throws if any block has already been fully
    ///     released, because a snapshot over an unmapped block would hand out handles that read
    ///     freed memory.
    /// </summary>
    public static Snapshot Create(IReadOnlyList<DriveBlock> driveBlocks)
    {
        ArgumentNullException.ThrowIfNull(driveBlocks);
        var taken = new List<DriveBlock>(driveBlocks.Count);
        foreach (var driveBlock in driveBlocks)
        {
            if (driveBlock.TryAddReference())
            {
                taken.Add(driveBlock);
                continue;
            }

            foreach (var alreadyTaken in taken)
            {
                alreadyTaken.Release();
            }

            throw new InvalidOperationException(
                $"Drive block {driveBlock.DriveLetter} was already released and cannot join a snapshot.");
        }

        return new Snapshot(new SnapshotRelease([.. taken]));
    }

    public DriveBlock GetDriveBlock(ushort driveOrdinal)
    {
        return _release.DriveBlocks[driveOrdinal];
    }

    /// <summary>Null when no current drive block has this letter.</summary>
    public DriveBlock? FindDriveBlock(char driveLetter)
    {
        foreach (var candidate in _release.DriveBlocks)
        {
            if (char.ToUpperInvariant(candidate.DriveLetter) == char.ToUpperInvariant(driveLetter))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    ///     Takes a reader's claim on this snapshot's blocks for the duration of one query. A
    ///     deterministic release waits for every outstanding borrow before it unmaps, so the
    ///     mapping a query is scanning stays valid until that query returns. Refused once a
    ///     release has begun: the blocks are on their way out at that point, so a new reader is
    ///     turned away rather than joined to the wait.
    /// </summary>
    internal SnapshotBorrow Borrow()
    {
        // Allocated before the count is taken, so nothing between the count and the caller's
        // hands can throw and strand a borrow that no one holds the means to return.
        var borrow = new SnapshotBorrow(this);
        if (!_release.TryTakeBorrow())
        {
            throw new ObjectDisposedException(nameof(Snapshot),
                "This snapshot has been released and can no longer be read.");
        }

        return borrow;
    }

    /// <summary>
    ///     Deterministic release, and the only one: it returns once this snapshot's blocks are
    ///     released, whether this call did the releasing or a finalizer that got there first is
    ///     still working through them, so a caller told the snapshot is closed never finds the
    ///     mapping still open. Waits for every outstanding borrow first, without holding a thread
    ///     while it does: that wait is as long as the query still inside the snapshot, and a
    ///     consumer disposing from a user-interface thread must not have that thread blocked for
    ///     it. The finalizer never reaches this method, which is what makes waiting here safe.
    /// </summary>
    [SuppressMessage("Design", "CA1816",
        Justification = "This is internal rather than a public Dispose; index disposal reaches it on " +
                        "the consumer's behalf, and tests use it for deterministic teardown.")]
    internal async ValueTask ReleaseNowAsync()
    {
        if (await _release.ReleaseAsync().ConfigureAwait(false))
        {
            GC.SuppressFinalize(this);
        }
    }

}

/// <summary>
///     Owns a snapshot's block references independently of the snapshot object's lifetime, so
///     index disposal can release a retired snapshot after garbage collection but before its
///     finalizer runs.
/// </summary>
internal sealed class SnapshotRelease
{
    readonly DriveBlock[] _driveBlocks;

    /// <summary>
    ///     Completes once the release that started has finished with every block. Continuations
    ///     run asynchronously so a waiter's remaining work never resumes on the finalizer thread
    ///     that completed this.
    /// </summary>
    readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Guards <see cref="_borrowCount" /> and <see cref="_borrowsDrained" />.</summary>
    readonly Lock _borrowGate = new();

    int _releaseState;

    int _borrowCount;

    /// <summary>
    ///     Created by the first asynchronous drain wait that finds borrows outstanding, and
    ///     completed when the last of them is returned. Null while nothing is waiting, so a
    ///     snapshot that is never released asynchronously never allocates one.
    /// </summary>
    TaskCompletionSource? _borrowsDrained;

    /// <summary>
    ///     A test seam, held per instance rather than statically so two snapshots never share it
    ///     and enabling test parallelism does not have to answer for it. Invoked once the release
    ///     flag is set and before the first block is unmapped, which is the window a competing
    ///     caller has to observe.
    /// </summary>
    internal Action? _releaseStartedForTest;

    internal SnapshotRelease(DriveBlock[] driveBlocks)
    {
        _driveBlocks = driveBlocks;
    }

    internal DriveBlock[] DriveBlocks => _driveBlocks;

    /// <summary>True from the moment a release begins, before the first block is unmapped.</summary>
    internal bool IsReleaseStarted => Volatile.Read(ref _releaseState) != 0;

    /// <summary>
    ///     True once the release has finished with every block. This, not
    ///     <see cref="IsReleaseStarted" />, is what says the blocks are no longer held: between
    ///     the two, the mappings and their files are still open.
    /// </summary>
    internal bool IsReleaseComplete => _completed.Task.IsCompleted;

    /// <summary>How many readers are holding this snapshot open right now.</summary>
    internal int OutstandingBorrowCount
    {
        get
        {
            lock (_borrowGate)
            {
                return _borrowCount;
            }
        }
    }

    /// <summary>
    ///     Records one reader, or reports that a release has begun and the blocks are on their
    ///     way out. Taken under the borrow gate rather than through an interlocked increment so a
    ///     borrow can never be admitted after the release flag is read but before the count moves.
    /// </summary>
    internal bool TryTakeBorrow()
    {
        lock (_borrowGate)
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
        lock (_borrowGate)
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
    ///     Releases every block, or reports that another caller already began. Never waits, for
    ///     either a competing release or an outstanding borrow: the snapshot finalizer calls
    ///     this, and a finalizer thread parked behind another thread stalls finalization for the
    ///     whole process. Waiting for borrows here would also be waiting for nothing, because a
    ///     borrow holds the snapshot itself and a borrowed snapshot is never collected.
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
    ///     blocks stay mapped until <see cref="CompleteRelease" /> runs, so a deterministic
    ///     caller can drain the readers that are already inside in between.
    /// </summary>
    bool TryBeginRelease()
    {
        lock (_borrowGate)
        {
            // Under the borrow gate so the flag a borrow reads and the flag a release sets are
            // the same decision: a borrow admitted just before this point is counted and waited
            // for, and one that arrives just after is refused.
            return Interlocked.Exchange(ref _releaseState, 1) == 0;
        }
    }

    void CompleteRelease()
    {
        try
        {
            _releaseStartedForTest?.Invoke();

            foreach (var driveBlock in _driveBlocks)
            {
                driveBlock.Release();
            }
        }
        finally
        {
            // Completed in a finally rather than after the loop. A block that throws partway
            // leaves the blocks after it mapped, which is what a failed release already cost;
            // a waiter parked forever on a completion that never arrives would be a new failure
            // on top of it. The exception still reaches this call's own caller.
            _completed.TrySetResult();
        }
    }

    /// <summary>
    ///     Releases, or waits out the release another caller already started, without blocking a
    ///     thread. Index disposal uses this, so it returns only once the blocks are actually
    ///     closed rather than once someone else has merely begun closing them, and only once
    ///     every reader that was inside the snapshot has left it. Returns whether this call did
    ///     the releasing.
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
    ///     front, so a snapshot released with no reader inside it allocates nothing.
    /// </summary>
    Task WaitForBorrowsToDrainAsync()
    {
        lock (_borrowGate)
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
