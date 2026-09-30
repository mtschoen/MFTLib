using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     The index-side stand-in for <see cref="IIndexWatchSource" />: every
///     <see cref="StartAsync" /> records its target and returns a fresh
///     <see cref="ScriptedDriveWatch" /> the test then drives. A start can be held on a
///     <see cref="TestGate" /> or made to fail, once, per call.
/// </summary>
internal sealed class FakeIndexWatchSource : IIndexWatchSource
{
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    readonly Lock _stateLock = new();
    readonly List<IndexWatchTarget> _starts = [];
    readonly List<ScriptedDriveWatch> _handles = [];
    readonly Dictionary<char, HeldStart> _holdsByDrive = [];
    readonly Dictionary<char, Exception> _failuresByDrive = [];
    HeldStart? _nextHold;
    Exception? _nextFailure;

    /// <summary>
    ///     Applied to every handle created after it is set. On by default, so a test that never
    ///     mentions it still fails on a double dispose.
    /// </summary>
    public bool ThrowOnSecondDispose { get; set; } = true;

    /// <summary>Every target <see cref="StartAsync" /> was called with, in order.</summary>
    public IReadOnlyList<IndexWatchTarget> Starts
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _starts];
            }
        }
    }

    /// <summary>Every handle returned, in order.</summary>
    public IReadOnlyList<ScriptedDriveWatch> Handles
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _handles];
            }
        }
    }

    public IReadOnlyList<IndexWatchTarget> StartsFor(char driveLetter) =>
        Starts.Where(target => char.ToUpperInvariant(target.DriveLetter) == char.ToUpperInvariant(driveLetter))
            .ToArray();

    /// <summary>The latest handle returned for the drive.</summary>
    public ScriptedDriveWatch HandleFor(char driveLetter) =>
        Handles.Last(handle => char.ToUpperInvariant(handle.DriveLetter) == char.ToUpperInvariant(driveLetter));

    /// <summary>
    ///     Parks the next start on <paramref name="gate" /> until it is released. With
    ///     <paramref name="observeToken" /> the start ends with
    ///     <see cref="OperationCanceledException" /> as soon as its token is cancelled; without it
    ///     the start ignores its token and returns a handle once released.
    /// </summary>
    public void HoldStart(TestGate gate, bool observeToken = true)
    {
        lock (_stateLock)
        {
            _nextHold = new HeldStart(gate, observeToken);
        }
    }

    /// <summary>Parks the next start of one drive on <paramref name="gate" />, whichever call order the starts arrive in.</summary>
    public void HoldStartFor(char driveLetter, TestGate gate, bool observeToken = true)
    {
        lock (_stateLock)
        {
            _holdsByDrive[char.ToUpperInvariant(driveLetter)] = new HeldStart(gate, observeToken);
        }
    }

    /// <summary>Makes the next start of one drive throw <paramref name="failure" />, whichever call order the starts arrive in.</summary>
    public void FailStartFor(char driveLetter, Exception failure)
    {
        lock (_stateLock)
        {
            _failuresByDrive[char.ToUpperInvariant(driveLetter)] = failure;
        }
    }

    /// <summary>Makes the next start throw <paramref name="failure" />.</summary>
    public void FailStart(Exception failure)
    {
        lock (_stateLock)
        {
            _nextFailure = failure;
        }
    }

    public async Task<IIndexDriveWatch> StartAsync(IndexWatchTarget target, CancellationToken cancellationToken)
    {
        HeldStart? hold;
        Exception? failure;
        lock (_stateLock)
        {
            _starts.Add(target);
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

        if (failure is not null)
        {
            throw failure;
        }

        var handle = new ScriptedDriveWatch(target, ThrowOnSecondDispose);
        lock (_stateLock)
        {
            _handles.Add(handle);
        }

        return handle;
    }

    sealed record HeldStart(TestGate Gate, bool ObserveToken);
}
