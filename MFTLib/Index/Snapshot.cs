using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     The set of drive blocks current at one moment. A <see cref="FileEntry" /> holds a
///     reference to its snapshot, keeping a block retired by a rescan mapped while that handle
///     remains reachable. The finalizer releases a retired snapshot once its handles become
///     unreachable while the index lives. <see cref="ReleaseNow" /> is the internal deterministic
///     release path used by index disposal and focused tests; released snapshots reject subsequent
///     handle reads.
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
    ///     on both the <see cref="ReleaseNow" /> path and the finalizer path. Deliberately started
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
    ///     mapping or a leftover file, which is what the caller already has. <see cref="ReleaseNow" />
    ///     keeps propagating, because a deterministic teardown failure is worth surfacing.
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
    ///     Deterministic release. Returns only once this snapshot's blocks are released, whether
    ///     this call did the releasing or a finalizer that got there first is still working
    ///     through them, so a caller told the snapshot is closed never finds the mapping still
    ///     open. Safe to block here because the finalizer path never calls this method.
    /// </summary>
    [SuppressMessage("Design", "CA1816",
        Justification = "ReleaseNow is internal rather than a public Dispose; index disposal reaches it " +
                         "on the consumer's behalf, and focused tests use it for deterministic teardown.")]
    internal void ReleaseNow()
    {
        if (_release.ReleaseAndWait())
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

    /// <summary>
    ///     Pulsed after <see cref="_completed" /> is set, so the synchronous waiter in
    ///     <see cref="ReleaseAndWait" /> can block on a monitor instead of on the task, which
    ///     keeps the deterministic path free of sync-over-async blocking.
    /// </summary>
    readonly object _completionGate = new();

    int _releaseState;

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

    /// <summary>
    ///     Releases every block, or reports that another caller already began. Never waits for
    ///     that other caller: the snapshot finalizer calls this, and a finalizer thread parked
    ///     behind another thread's release stalls finalization for the whole process.
    /// </summary>
    internal bool Release()
    {
        if (Interlocked.Exchange(ref _releaseState, 1) != 0)
        {
            return false;
        }

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
            lock (_completionGate)
            {
                Monitor.PulseAll(_completionGate);
            }
        }

        return true;
    }

    /// <summary>
    ///     Releases, or waits out the release another caller already started, without blocking a
    ///     thread. Index disposal uses this, so it returns only once the blocks are actually
    ///     closed rather than once someone else has merely begun closing them.
    /// </summary>
    internal async ValueTask ReleaseAsync()
    {
        if (!Release())
        {
            await _completed.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The blocking twin of <see cref="ReleaseAsync" />, for the synchronous deterministic
    ///     path. Returns whether this call did the releasing. Only ever reached from a caller that
    ///     is not the finalizer thread, which is what makes waiting here safe.
    /// </summary>
    internal bool ReleaseAndWait()
    {
        if (Release())
        {
            return true;
        }

        lock (_completionGate)
        {
            // Checked under the gate so a completion that lands between the check and the wait
            // cannot be missed: the releasing thread pulses only after taking the same lock.
            while (!_completed.Task.IsCompleted)
            {
                Monitor.Wait(_completionGate);
            }
        }

        return false;
    }
}
