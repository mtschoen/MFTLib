using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     The index-side stand-in for <see cref="IIndexWatchSource" />. It mirrors the production
///     seam's own per-drive state, one arm generation and one stop-requested flag per drive, so an
///     item published for a drive that has since been disarmed or re-armed is dropped exactly as
///     the real source drops it. It models no wire arm epoch: the epoch lives a layer below this
///     seam, inside the broker client's demux, and nothing here can observe it.
/// </summary>
internal sealed class FakeIndexWatchSource : IIndexWatchSource, IDisposable
{
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    readonly Lock _stateLock = new();
    readonly Channel<PendingItem> _items = Channel.CreateUnbounded<PendingItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    readonly Dictionary<char, int> _armGenerationsByDrive = [];
    readonly HashSet<char> _stopRequestedDrives = [];
    readonly List<char> _disarmedDrives = [];
    readonly List<IndexWatchTarget> _armedDrives = [];
    readonly List<string> _watchOperations = [];
    readonly List<SessionSignals> _sessions = [];
    readonly TaskCompletionSource _sourceCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource _wedgeRelease = new();

    TaskCompletionSource? _holdRelease;
    Exception? _nextArmFailure;
    Exception? _nextDisarmFailure;
    bool _ignoreCancellation;
    bool _streamLive;
    int _liveSourceCount;
    int _sourceInvocationCount;

    public int SourceInvocationCount => Volatile.Read(ref _sourceInvocationCount);

    /// <summary>Source iterators that have entered but not yet left their finally.</summary>
    public int LiveSourceCount => Volatile.Read(ref _liveSourceCount);

    public bool SourceCancelled { get; private set; }

    /// <summary>Runs inside the source iterator's finally, before the live count drops.</summary>
    public Action? SourceEnding { get; set; }

    public IReadOnlyList<char> DisarmedDrives
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _disarmedDrives];
            }
        }
    }

    public IReadOnlyList<IndexWatchTarget> ArmedDrives
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _armedDrives];
            }
        }
    }

    /// <summary>Every per-drive call in the order it arrived, so a test can pin disarm before arm.</summary>
    public IReadOnlyList<string> WatchOperations
    {
        get
        {
            lock (_stateLock)
            {
                return [.. _watchOperations];
            }
        }
    }

    public IAsyncEnumerable<WatchStreamItem> StartWatching(IReadOnlyList<IndexWatchTarget> targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return StreamAsync(targets, cancellationToken);
    }

    public Task ArmDriveAsync(IndexWatchTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            if (_nextArmFailure is { } armFailure)
            {
                _nextArmFailure = null;
                throw armFailure;
            }

            ThrowIfNoStreamLocked("arm");
            var driveLetter = char.ToUpperInvariant(target.DriveLetter);
            _armGenerationsByDrive[driveLetter] = _armGenerationsByDrive.GetValueOrDefault(driveLetter) + 1;
            _stopRequestedDrives.Remove(driveLetter);
            _armedDrives.Add(target);
            _watchOperations.Add($"arm:{driveLetter}");
        }

        return Task.CompletedTask;
    }

    public Task DisarmDriveAsync(char driveLetter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            ThrowIfNoStreamLocked("disarm");
            var normalizedLetter = char.ToUpperInvariant(driveLetter);
            _armGenerationsByDrive[normalizedLetter] = _armGenerationsByDrive.GetValueOrDefault(normalizedLetter) + 1;
            _stopRequestedDrives.Add(normalizedLetter);
            _disarmedDrives.Add(normalizedLetter);
            _watchOperations.Add($"disarm:{normalizedLetter}");

            // Thrown after the state change, which is the shape the broker source fails in: it
            // retires the reader and bumps the arm generation before anything it awaits can throw,
            // so a failed disarm leaves the drive genuinely stopped.
            if (_nextDisarmFailure is { } disarmFailure)
            {
                _nextDisarmFailure = null;
                throw disarmFailure;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Writes one item and waits until the pump has consumed or dropped it.</summary>
    public Task PublishAsync(WatchStreamItem item)
    {
        return Queue(item).WaitAsync(HangGuard);
    }

    /// <summary>
    ///     Writes one item and hands back the task that completes when the stream consumes or
    ///     drops it, so a test can leave an item sitting on the merged channel across a rescan.
    /// </summary>
    public Task Queue(WatchStreamItem item)
    {
        var consumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock)
        {
            var driveLetter = DriveLetterOf(item);
            _items.Writer.TryWrite(new PendingItem(item, driveLetter,
                _armGenerationsByDrive.GetValueOrDefault(driveLetter), consumed));
        }

        return consumed.Task;
    }

    /// <summary>Stops the stream reading its channel, so published items stay queued on it.</summary>
    public void HoldItemsUnread()
    {
        lock (_stateLock)
        {
            _holdRelease ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void ReleaseHeldItems()
    {
        TaskCompletionSource? release;
        lock (_stateLock)
        {
            release = _holdRelease;
            _holdRelease = null;
        }

        release?.TrySetResult();
    }

    /// <summary>Makes the next arm fail, which is what a source that cannot resume looks like.</summary>
    public void FailNextArm(Exception failure)
    {
        lock (_stateLock)
        {
            _nextArmFailure = failure;
        }
    }

    /// <summary>Makes the next disarm fail, which is a source that cannot stop a drive.</summary>
    public void FailNextDisarm(Exception failure)
    {
        lock (_stateLock)
        {
            _nextDisarmFailure = failure;
        }
    }

    /// <summary>Makes the iterator ignore its token, which is what a wedged source looks like.</summary>
    public void IgnoreCancellation()
    {
        lock (_stateLock)
        {
            _ignoreCancellation = true;
        }
    }

    public void ReleaseWedgedSource()
    {
        lock (_stateLock)
        {
            _ignoreCancellation = false;
        }

        _wedgeRelease.Cancel();
    }

    public Task FaultSourceAsync(Exception exception)
    {
        _items.Writer.TryComplete(exception);
        return SourceEndedAsync();
    }

    public Task CompleteSourceAsync()
    {
        _items.Writer.TryComplete();
        return _sourceCompleted.Task.WaitAsync(HangGuard);
    }

    /// <summary>
    ///     Completed from the top of the source iterator with the targets it was given. Once the
    ///     latest stream has ended this waits for the next one, so a caller that restarts the
    ///     index can await the fresh stream rather than reading the finished one's signal.
    /// </summary>
    public Task<IReadOnlyList<IndexWatchTarget>> SourceStartedAsync()
    {
        lock (_stateLock)
        {
            if (_sessions.Count == 0 || _sessions[^1].Ended.Task.IsCompleted)
            {
                _sessions.Add(new SessionSignals());
            }

            return _sessions[^1].Started.Task.WaitAsync(HangGuard);
        }
    }

    /// <summary>Completed from the source iterator's finally, for the latest stream.</summary>
    public Task SourceEndedAsync()
    {
        lock (_stateLock)
        {
            if (_sessions.Count == 0)
            {
                _sessions.Add(new SessionSignals());
            }

            return _sessions[^1].Ended.Task.WaitAsync(HangGuard);
        }
    }

    public void Dispose()
    {
        _wedgeRelease.Dispose();
    }

    async IAsyncEnumerable<WatchStreamItem> StreamAsync(IReadOnlyList<IndexWatchTarget> targets,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SessionSignals signals;
        CancellationToken readToken;
        lock (_stateLock)
        {
            _sourceInvocationCount++;
            _streamLive = true;
            readToken = _ignoreCancellation ? _wedgeRelease.Token : cancellationToken;
            signals = ClaimSignalsLocked();
        }

        Interlocked.Increment(ref _liveSourceCount);
        signals.Started.TrySetResult([.. targets]);
        try
        {
            await foreach (var pending in _items.Reader.ReadAllAsync(readToken).ConfigureAwait(false))
            {
                await WaitWhileHeldAsync(readToken).ConfigureAwait(false);
                if (IsStale(pending))
                {
                    pending.Consumed.TrySetResult();
                    continue;
                }

                try
                {
                    yield return pending.Item;
                }
                finally
                {
                    pending.Consumed.TrySetResult();
                }
            }
        }
        finally
        {
            SourceCancelled = cancellationToken.IsCancellationRequested;
            lock (_stateLock)
            {
                _streamLive = false;
            }

            SourceEnding?.Invoke();
            Interlocked.Decrement(ref _liveSourceCount);
            if (!SourceCancelled)
            {
                _sourceCompleted.TrySetResult();
            }

            signals.Ended.TrySetResult();
        }
    }

    async Task WaitWhileHeldAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TaskCompletionSource? release;
            lock (_stateLock)
            {
                release = _holdRelease;
            }

            if (release is null)
            {
                return;
            }

            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    bool IsStale(PendingItem pending)
    {
        lock (_stateLock)
        {
            return _stopRequestedDrives.Contains(pending.DriveLetter) ||
                   _armGenerationsByDrive.GetValueOrDefault(pending.DriveLetter) != pending.ArmGeneration;
        }
    }

    SessionSignals ClaimSignalsLocked()
    {
        if (_sessions.Count == 0 || _sessions[^1].Started.Task.IsCompleted)
        {
            _sessions.Add(new SessionSignals());
        }

        return _sessions[^1];
    }

    void ThrowIfNoStreamLocked(string operation)
    {
        if (!_streamLive)
        {
            throw new InvalidOperationException($"No stream is running, so there is no drive to {operation}.");
        }
    }

    static char DriveLetterOf(WatchStreamItem item)
    {
        return item switch
        {
            JournalBatch batch => char.ToUpperInvariant(batch.DriveLetter),
            DriveWatchFailure failure => char.ToUpperInvariant(failure.DriveLetter),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item, "Unknown watch stream item.")
        };
    }

    sealed record PendingItem(WatchStreamItem Item, char DriveLetter, int ArmGeneration,
        TaskCompletionSource Consumed);

    sealed class SessionSignals
    {
        public TaskCompletionSource<IReadOnlyList<IndexWatchTarget>> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
