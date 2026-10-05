using MFTLib;
using MFTLib.Index;

namespace MFTLibTestExtensions;

/// <summary>
///     A scripted stand-in for the live watch an index runs beside its scan: every start the index
///     requests is recorded and answered with a fresh <see cref="ScriptedDriveWatch" /> the test
///     then drives. Starts can be made to fail, per drive or once, or held until a test releases them.
/// </summary>
public sealed class ScriptedWatchSource : IIndexWatchSource
{
    internal static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    readonly Lock _stateLock = new();
    readonly List<IndexWatchTarget> _targets = [];
    readonly List<ScriptedDriveWatch> _watches = [];
    readonly Dictionary<char, HeldStart> _holdsByDrive = [];
    readonly Dictionary<char, Exception> _failuresByDrive = [];
    readonly Dictionary<char, List<TaskCompletionSource<ScriptedDriveWatch>>> _startWaiters = [];
    HeldStart? _nextHold;
    Exception? _nextFailure;

    /// <summary>When true, every started watch queues the caught-up marker at once. Default false.</summary>
    public bool CatchUpOnStart { get; init; }

    /// <summary>Consulted on every start: a non-null result is thrown instead of starting.</summary>
    public Func<char, Exception?>? StartFailure { get; set; }

    /// <summary>Every start requested, in order, including starts that were then failed or held.</summary>
    public IReadOnlyList<ScriptedWatchStart> Starts
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _targets.Select(target =>
                    new ScriptedWatchStart(target.DriveLetter, new UsnJournalCursor(target.JournalId, target.NextUsn)))];
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
    public ScriptedDriveWatch WatchFor(char driveLetter) =>
        Watches.Last(watch => Same(watch.DriveLetter, driveLetter));

    /// <summary>Completes with the next watch handed out for the drive after this call.</summary>
    /// <param name="driveLetter">The drive to wait for.</param>
    /// <param name="cancellationToken">Ends the wait with <see cref="OperationCanceledException" />.</param>
    /// <returns>The watch the index received.</returns>
    public Task<ScriptedDriveWatch> WaitForStartAsync(char driveLetter, CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<ScriptedDriveWatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock)
        {
            var key = char.ToUpperInvariant(driveLetter);
            if (!_startWaiters.TryGetValue(key, out var waiters))
            {
                waiters = [];
                _startWaiters[key] = waiters;
            }

            waiters.Add(waiter);
        }

        return waiter.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Makes the next start throw <paramref name="failure" />.</summary>
    /// <param name="failure">The exception the start throws.</param>
    public void FailNextStart(Exception failure)
    {
        lock (_stateLock)
        {
            _nextFailure = failure;
        }
    }

    /// <summary>Makes the next start of one drive throw <paramref name="failure" />, whichever call order the starts arrive in.</summary>
    /// <param name="driveLetter">The drive whose next start fails.</param>
    /// <param name="failure">The exception the start throws.</param>
    public void FailNextStartFor(char driveLetter, Exception failure)
    {
        lock (_stateLock)
        {
            _failuresByDrive[char.ToUpperInvariant(driveLetter)] = failure;
        }
    }

    /// <summary>The targets requested for one drive, in order.</summary>
    internal IReadOnlyList<IndexWatchTarget> TargetsFor(char driveLetter) =>
        Targets.Where(target => Same(target.DriveLetter, driveLetter)).ToArray();

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
    internal void HoldStartFor(char driveLetter, TestGate gate, bool observeToken = true)
    {
        lock (_stateLock)
        {
            _holdsByDrive[char.ToUpperInvariant(driveLetter)] = new HeldStart(gate, observeToken);
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
            var driveLetter = char.ToUpperInvariant(target.DriveLetter);
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

        failure ??= StartFailure?.Invoke(target.DriveLetter);
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
        List<TaskCompletionSource<ScriptedDriveWatch>>? waiters = null;
        lock (_stateLock)
        {
            var previous = _watches.LastOrDefault(existing => Same(existing.DriveLetter, target.DriveLetter));
            if (previous is { DisposeCount: 0 })
            {
                throw new InvalidOperationException(
                    $"A watch for drive {target.DriveLetter} was started while the previous one was still running.");
            }

            _watches.Add(watch);
            if (_startWaiters.Remove(char.ToUpperInvariant(target.DriveLetter), out var found))
            {
                waiters = found;
            }
        }

        if (CatchUpOnStart)
        {
            _ = watch.QueueCaughtUp();
        }

        foreach (var waiter in waiters ?? [])
        {
            waiter.TrySetResult(watch);
        }

        return watch;
    }

    static bool Same(char left, char right) => char.ToUpperInvariant(left) == char.ToUpperInvariant(right);

    sealed record HeldStart(TestGate Gate, bool ObserveToken);
}
