using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     A scripted stand-in for the live watch an index runs beside its scan: every start the index
///     requests is recorded and answered with a fresh <see cref="ScriptedVolumeWatch" /> the test
///     then drives. A start fails when the <see cref="StartFailure" /> callback returns an exception.
/// </summary>
public sealed class ScriptedWatchSource
{
    readonly Lock _stateLock = new();
    readonly List<IndexWatchTarget> _targets = [];
    readonly List<ScriptedVolumeWatch> _watches = [];
    readonly Dictionary<VolumeKey, HeldStart> _holdsByVolume = [];
    readonly Dictionary<VolumeKey, Exception> _failuresByVolume = [];
    readonly Dictionary<VolumeKey, List<StartWaiter>> _startWaiters = [];
    ScriptedWatchSourceAdapter? _indexSource;
    HeldStart? _nextHold;
    Exception? _nextFailure;

    /// <summary>When true, every started watch queues the caught-up marker at once. Default false.</summary>
    public bool CatchUpOnStart { get; init; }

    /// <summary>
    ///     Consulted on every start: a non-null result is thrown instead of starting. Settable because a
    ///     test changes it while the index is running, to make a later restart fail.
    /// </summary>
    public Func<VolumeKey, Exception?>? StartFailure { get; set; }

    /// <summary>Every start requested, in order, including starts that were then failed or held.</summary>
    public IReadOnlyList<ScriptedWatchStart> Starts
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _targets.Select(target =>
                    new ScriptedWatchStart(target.Volume, new SyntheticJournalCursor(target.JournalIdentifier, target.NextUsn)))];
            }
        }
    }

    /// <summary>Every watch handed to the index, in order.</summary>
    public IReadOnlyList<ScriptedVolumeWatch> Watches
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

    /// <summary>The most recent watch started for the volume, matched without regard to letter case.</summary>
    /// <param name="volume">The volume to look up.</param>
    /// <returns>The most recent watch for that volume.</returns>
    /// <exception cref="InvalidOperationException">No watch was handed out for the volume.</exception>
    public ScriptedVolumeWatch WatchFor(VolumeKey volume)
    {
        volume.Validated();
        return Watches.Last(watch => Same(watch.Volume, volume));
    }

    /// <summary>
    ///     Completes with the watch handed out for the volume as its <paramref name="startNumber" />th, counting
    ///     from one in the order the watches were handed out. A watch handed out before this call satisfies
    ///     the wait at once, so a test may trigger the start and wait afterwards without racing it. A volume
    ///     the index restarts gets a new watch each time: the first start is number one, the restart number
    ///     two. A start that failed or is still held has handed out no watch and is not counted.
    /// </summary>
    /// <param name="volume">The volume to wait for, matched without regard to letter case.</param>
    /// <param name="startNumber">Which watch of the volume to wait for, one for the first.</param>
    /// <param name="cancellationToken">Ends the wait with <see cref="OperationCanceledException" />.</param>
    /// <returns>The watch the index received.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startNumber" /> is less than one.</exception>
    public Task<ScriptedVolumeWatch> WaitForStartAsync(VolumeKey volume, int startNumber,
        CancellationToken cancellationToken)
    {
        volume.Validated();
        ArgumentOutOfRangeException.ThrowIfLessThan(startNumber, 1);
        var waiter = new StartWaiter(startNumber);
        lock (_stateLock)
        {
            var handedOut = _watches.Where(watch => Same(watch.Volume, volume)).ToList();
            if (handedOut.Count >= startNumber)
            {
                return Task.FromResult(handedOut[startNumber - 1]);
            }

            var key = volume;
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

    /// <summary>Makes the next start of one volume throw <paramref name="failure" />, whichever call order the starts arrive in.</summary>
    /// <param name="volume">The volume whose next start fails.</param>
    /// <param name="failure">The exception the start throws.</param>
    internal void FailNextStartFor(VolumeKey volume, Exception failure)
    {
        lock (_stateLock)
        {
            _failuresByVolume[volume] = failure;
        }
    }

    /// <summary>The targets requested for one volume, in order.</summary>
    internal IReadOnlyList<IndexWatchTarget> TargetsFor(VolumeKey volume) =>
        Targets.Where(target => Same(target.Volume, volume)).ToArray();

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

    /// <summary>Parks the next start of one volume on <paramref name="gate" />, whichever call order the starts arrive in.</summary>
    internal void HoldStartFor(VolumeKey volume, TestGate gate, bool observeToken = true)
    {
        lock (_stateLock)
        {
            _holdsByVolume[volume] = new HeldStart(gate, observeToken);
        }
    }

    /// <summary>Records the start, applies any hold or failure scripted for it, and hands out the watch.</summary>
    internal async Task<ScriptedVolumeWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken)
    {
        HeldStart? hold;
        Exception? failure;
        lock (_stateLock)
        {
            _targets.Add(target);
            var volume = target.Volume;
            if (!_holdsByVolume.Remove(volume, out hold))
            {
                hold = _nextHold;
                _nextHold = null;
            }

            if (!_failuresByVolume.Remove(volume, out failure))
            {
                failure = _nextFailure;
                _nextFailure = null;
            }
        }

        if (hold is not null)
        {
            hold.Gate.MarkEntered();
            await hold.Gate.WaitForReleaseAsync(hold.ObserveToken ? cancellationToken : CancellationToken.None)
                .WaitAsync(TestTimeouts.HangGuard, CancellationToken.None).ConfigureAwait(false);
        }

        failure ??= StartFailure?.Invoke(target.Volume);
        if (failure is not null)
        {
            throw failure;
        }

        return HandOut(target);
    }

    /// <summary>The source as the index starts watches from it, through an adapter so no public type names the internal interface.</summary>
    internal IIndexWatchSource IndexSource => _indexSource ??= new ScriptedWatchSourceAdapter(this);

    ScriptedVolumeWatch HandOut(IndexWatchTarget target)
    {
        var watch = new ScriptedVolumeWatch(target);
        List<StartWaiter> satisfied = [];
        lock (_stateLock)
        {
            var previous = _watches.LastOrDefault(existing => Same(existing.Volume, target.Volume));
            if (previous is { DisposeCount: 0 })
            {
                throw new InvalidOperationException(
                    $"A watch for volume {target.Volume} was started while the previous one was still running.");
            }

            _watches.Add(watch);
            if (_startWaiters.TryGetValue(target.Volume, out var waiters))
            {
                var startNumber = _watches.Count(existing => Same(existing.Volume, target.Volume));
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

    static bool Same(VolumeKey left, VolumeKey right) => left == right;

    sealed record HeldStart(TestGate Gate, bool ObserveToken);

    sealed class StartWaiter(int startNumber)
    {
        public int StartNumber { get; } = startNumber;

        public TaskCompletionSource<ScriptedVolumeWatch> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
