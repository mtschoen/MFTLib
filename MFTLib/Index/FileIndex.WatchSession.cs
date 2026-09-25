namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     One watch session's cancellation, pump, source, and targets held together, so a start
    ///     racing a stop cannot claim one and publish another. A single
    ///     <see cref="FileIndex._watchSession" /> field is claimed, read, and cleared under
    ///     <see cref="FileIndex._stateLock" />.
    /// </summary>
    sealed class WatchSession
    {
        readonly List<IndexWatchTarget> _targets;
        readonly Lock _targetsLock = new();
        readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool _streamStarted;

        public WatchSession(CancellationTokenSource cancellation, CancellationTokenSource teardown,
            IIndexWatchSource source, IReadOnlyList<IndexWatchTarget> targets, CancellationToken callerToken)
        {
            Cancellation = cancellation;
            _teardown = teardown;
            Source = source;
            CallerToken = callerToken;
            _targets = [.. targets];
        }

        public CancellationTokenSource Cancellation { get; }

        // Never disposed: it has no timer and links nothing, and a stop's token registration may
        // cancel it after the session has been released.
        readonly CancellationTokenSource _teardown;

        /// <summary>
        ///     Bounds how long the source's cleanup may wait on something outside the process once
        ///     <see cref="Cancellation" /> has ended the stream. Cancelled only by
        ///     <see cref="CancelTeardown" />, which a <see cref="FileIndex.StopWatchingAsync" /> runs
        ///     when its own token is cancelled.
        /// </summary>
        public CancellationToken TeardownToken => _teardown.Token;

        public void CancelTeardown()
        {
            _teardown.Cancel();
        }

        // The exact source the pump is reading, so a rescan reaches the arm and disarm
        // operations of the stream in flight rather than of some other instance.
        public IIndexWatchSource Source { get; }

        /// <summary>
        ///     The drives this session is currently watching, including both initial targets and
        ///     drives adopted and armed mid-session: who a catch-up wait covers, and which drive
        ///     letters count as watched.
        /// </summary>
        public IndexWatchTarget[] Targets
        {
            get
            {
                lock (_targetsLock)
                {
                    return _targets.ToArray();
                }
            }
        }

        /// <summary>The token this session's source was linked to, which a restart relinks to.</summary>
        public CancellationToken CallerToken { get; }

        public WatchSessionFaults Faults { get; } = new();

        public Task Pump { get; set; } = Task.CompletedTask;

        /// <summary>
        ///     Set once the pump has received its first item. A source cannot yield without a
        ///     running stream, so from then on a <see cref="WatchStreamNotRunningException" /> from
        ///     this session's source means its stream has been released.
        /// </summary>
        public bool StreamStarted => Volatile.Read(ref _streamStarted);

        /// <summary>
        ///     Set under <see cref="FileIndex._stateLock" /> once the pump has stopped reading: when
        ///     it decides no watched drive remains, before the source releases its stream, and when
        ///     the stream ends for any other reason. A rescan reads it under the same lock to choose
        ///     between re-arming its drive on this session and starting a fresh one.
        /// </summary>
        public bool Ended { get; set; }

        /// <summary>
        ///     Completes once this session's source reports that its stream accepts per-drive arm
        ///     and disarm, or once the pump receives its first item, whichever is first. It belongs
        ///     to this session alone, so no other session's source can complete it. The pump
        ///     settles it before it finishes, faulted or cancelled when the stream failed, was
        ///     cancelled, or ended without ever becoming ready, so nothing waiting on it is stranded.
        /// </summary>
        public Task Ready => _ready.Task;

        public void MarkReady() => _ready.TrySetResult();

        public void MarkStreamStarted()
        {
            Volatile.Write(ref _streamStarted, true);
            MarkReady();
        }

        /// <summary>
        ///     Settles <see cref="Ready" /> for a stream that is over, which is a no-op once it has
        ///     completed. A startup failure faults it with that failure, a cancellation cancels it,
        ///     and a stream that simply ended faults it, since it never accepted a drive.
        /// </summary>
        public void SettleReadinessForEndedStream(Exception? failure, CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                _ready.TrySetException(failure);
            }
            else if (cancellationToken.IsCancellationRequested)
            {
                _ready.TrySetCanceled(cancellationToken);
            }
            else
            {
                _ready.TrySetException(new InvalidOperationException(
                    "The watch source ended its stream before it was ready for drives to be armed."));
            }

            // Marked observed here because a start abandoned by its own token, and every rescan,
            // reads only whether readiness succeeded. The failure is not lost: a start still
            // waiting rethrows it, and the pump has already recorded and announced it.
            _ = _ready.Task.Exception;
        }

        public void RegisterTarget(IndexWatchTarget target)
        {
            lock (_targetsLock)
            {
                var upperLetter = char.ToUpperInvariant(target.DriveLetter);
                var index = _targets.FindIndex(t => char.ToUpperInvariant(t.DriveLetter) == upperLetter);
                if (index >= 0)
                {
                    _targets[index] = target;
                }
                else
                {
                    _targets.Add(target);
                }
            }
        }

        public bool ContainsTarget(char driveLetter)
        {
            lock (_targetsLock)
            {
                var upperLetter = char.ToUpperInvariant(driveLetter);
                return _targets.Any(t => char.ToUpperInvariant(t.DriveLetter) == upperLetter);
            }
        }
    }
}
