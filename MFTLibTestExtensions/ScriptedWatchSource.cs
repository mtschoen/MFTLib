using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     A scripted stand-in for the live watch an index runs beside its scan: every start the index
///     requests is recorded and answered with a fresh <see cref="ScriptedDriveWatch" /> the test
///     then drives. A start fails when the <see cref="StartFailure" /> callback returns an exception.
/// </summary>
public sealed class ScriptedWatchSource : IIndexWatchSource
{
    internal static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    readonly Lock _stateLock = new();
    readonly List<IndexWatchTarget> _targets = [];
    readonly List<ScriptedDriveWatch> _watches = [];
    readonly Dictionary<DriveKey, HeldStart> _holdsByDrive = [];
    readonly Dictionary<DriveKey, Exception> _failuresByDrive = [];
    readonly Dictionary<DriveKey, List<StartWaiter>> _startWaiters = [];
    HeldStart? _nextHold;
    Exception? _nextFailure;

    /// <summary>When true, every started watch queues the caught-up marker at once. Default false.</summary>
    public bool CatchUpOnStart { get; init; }

    /// <summary>Consulted on every start: a non-null result is thrown instead of starting.</summary>
    public Func<DriveKey, Exception?>? StartFailure { get; set; }

    /// <summary>Every start requested, in order, including starts that were then failed or held.</summary>
    public IReadOnlyList<ScriptedWatchStart> Starts
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _targets.Select(target =>
                    new ScriptedWatchStart(target.Drive, new SyntheticJournalCursor(target.JournalIdentifier, target.NextUsn)))];
            }
        }
    }

    /// <summary>Every watch handed to the index, in order.</summary>
    public IReadOnlyList<ScriptedDriveWatch> Watches
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _watches];
            }
        }
    }

    /// <summary>Every target a start was requested with, in order.</summary>
    internal IReadOnlyList<IndexWatchTarget> Targets
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _targets];
            }
        }
    }

    /// <summary>The most recent watch started for the drive, matched without regard to letter case.</summary>
    /// <param name="driveLetter">The drive to look up.</param>
    /// <returns>The most recent watch for that drive.</returns>
    /// <exception cref="InvalidOperationException">No watch was handed out for the drive.</exception>
    public ScriptedDriveWatch WatchFor(DriveKey driveLetter)
    {
        driveLetter.Validated();
        return Watches.Last(watch => Same(watch.Drive, driveLetter));
    }

    /// <summary>
    ///     Completes with the watch handed out for the drive as its <paramref name="startNumber" />th, counting
    ///     from one in the order the watches were handed out. A watch handed out before this call satisfies
    ///     the wait at once, so a test may trigger the start and wait afterwards without racing it. A drive
    ///     the index restarts gets a new watch each time: the first start is number one, the restart number
    ///     two. A start that failed or is still held has handed out no watch and is not counted.
    /// </summary>
    /// <param name="driveLetter">The drive to wait for, matched without regard to letter case.</param>
    /// <param name="startNumber">Which watch of the drive to wait for, one for the first.</param>
    /// <param name="cancellationToken">Ends the wait with <see cref="OperationCanceledException" />.</param>
    /// <returns>The watch the index received.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startNumber" /> is less than one.</exception>
    public Task<ScriptedDriveWatch> WaitForStartAsync(DriveKey driveLetter, int startNumber,
        CancellationToken cancellationToken)
    {
        driveLetter.Validated();
        ArgumentOutOfRangeException.ThrowIfLessThan(startNumber, 1);
        var waiter = new StartWaiter(startNumber);
        lock (_stateLock)
        {
            var handedOut = _watches.Where(watch => Same(watch.Drive, driveLetter)).ToList();
            if (handedOut.Count >= startNumber)
            {
                return Task.FromResult(handedOut[startNumber - 1]);
            }

            var key = driveLetter;
            if (!_startWaiters.TryGetValue(key, out var waiters))
            {
                waiters = [];
                _startWaiters[key] = waiters;
            }

            waiters.Add(waiter);
        }

        return waiter.Completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Makes the next start throw <paramref name="failure" />.</summary>
    /// <param name="failure">The exception the start throws.</param>
    internal void FailNextStart(Exception failure)
    {
        lock (_stateLock)
        {
            _nextFailure = failure;
        }
    }

    /// <summary>Makes the next start of one drive throw <paramref name="failure" />, whichever call order the starts arrive in.</summary>
    /// <param name="driveLetter">The drive whose next start fails.</param>
    /// <param name="failure">The exception the start throws.</param>
    internal void FailNextStartFor(DriveKey driveLetter, Exception failure)
    {
        lock (_stateLock)
        {
            _failuresByDrive[driveLetter] = failure;
        }
    }

    /// <summary>The targets requested for one drive, in order.</summary>
    internal IReadOnlyList<IndexWatchTarget> TargetsFor(DriveKey driveLetter) =>
        Targets.Where(target => Same(target.Drive, driveLetter)).ToArray();

    /// <summary>
    ///     Parks the next start on <paramref name="gate" /> until it is released. With
    ///     <paramref name="observeToken" /> the start ends with
    ///     <see cref="OperationCanceledException" /> as soon as its token is cancelled; without it
    ///     the start ignores its token and returns a watch once released.
    /// </summary>
    internal void HoldStart(TestGate gate, bool observeToken = true)
    {
        lock (_stateLock)
        {
            _nextHold = new HeldStart(gate, observeToken);
        }
    }

    /// <summary>Parks the next start of one drive on <paramref name="gate" />, whichever call order the starts arrive in.</summary>
    internal void HoldStartFor(DriveKey driveLetter, TestGate gate, bool observeToken = true)
    {
        lock (_stateLock)
        {
            _holdsByDrive[driveLetter] = new HeldStart(gate, observeToken);
        }
    }

    /// <summary>Records the start, applies any hold or failure scripted for it, and hands out the watch.</summary>
    internal async Task<ScriptedDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken)
    {
        HeldStart? hold;
        Exception? failure;
        lock (_stateLock)
        {
            _targets.Add(target);
            var driveLetter = target.Drive;
            if (!_holdsByDrive.Remove(driveLetter, out hold))
            {
                hold = _nextHold;
                _nextHold = null;
            }

            if (!_failuresByDrive.Remove(driveLetter, out failure))
            {
                failure = _nextFailure;
                _nextFailure = null;
            }
        }

        if (hold is not null)
        {
            hold.Gate.MarkEntered();
            await hold.Gate.WaitForReleaseAsync(hold.ObserveToken ? cancellationToken : CancellationToken.None)
                .WaitAsync(HangGuard, CancellationToken.None).ConfigureAwait(false);
        }

        failure ??= StartFailure?.Invoke(target.Drive);
        if (failure is not null)
        {
            throw failure;
        }

        return HandOut(target);
    }

    async Task<IIndexDriveWatch> IIndexWatchSource.StartAsync(IndexWatchTarget target,
        CancellationToken cancellationToken)
    {
        return await StartAsync(target, cancellationToken).ConfigureAwait(false);
    }

    ScriptedDriveWatch HandOut(IndexWatchTarget target)
    {
        var watch = new ScriptedDriveWatch(target);
        List<StartWaiter> satisfied = [];
        lock (_stateLock)
        {
            var previous = _watches.LastOrDefault(existing => Same(existing.Drive, target.Drive));
            if (previous is { DisposeCount: 0 })
            {
                throw new InvalidOperationException(
                    $"A watch for drive {target.Drive} was started while the previous one was still running.");
            }

            _watches.Add(watch);
            if (_startWaiters.TryGetValue(target.Drive, out var waiters))
            {
                var startNumber = _watches.Count(existing => Same(existing.Drive, target.Drive));
                satisfied = [.. waiters.Where(waiter => waiter.StartNumber <= startNumber)];
                waiters.RemoveAll(satisfied.Contains);
            }
        }

        if (CatchUpOnStart)
        {
            _ = watch.QueueCaughtUp();
        }

        foreach (var waiter in satisfied)
        {
            waiter.Completion.TrySetResult(watch);
        }

        return watch;
    }

    static bool Same(DriveKey left, DriveKey right) => left == right;

    sealed record HeldStart(TestGate Gate, bool ObserveToken);

    sealed class StartWaiter(int startNumber)
    {
        public int StartNumber { get; } = startNumber;

        public TaskCompletionSource<ScriptedDriveWatch> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
