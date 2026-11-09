using System.Collections.Concurrent;
using System.Threading.Channels;
using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A volume that can be taken away and returned, the fake clock the index waits on while its
///     automatic recovery retries, and the volume arrivals the test raises in place of the
///     operating system's.
/// </summary>
sealed partial class WatchHarness
{
    readonly ConcurrentDictionary<char, bool> _absentDrives = [];
    readonly Channel<char> _volumeFactsReads = Channel.CreateUnbounded<char>();
    readonly ConcurrentQueue<string> _diagnostics = [];
    Action? _afterVolumeFactsCaptured;
    Action? _beforeVolumeFactsCaptured;
    int _flappingProbes;

    /// <summary>
    ///     Inside the next volume facts read, before the facts are captured, makes the volume present
    ///     without any arrival: a scan failed because the volume was gone, and it came back before
    ///     the recovery probed it.
    /// </summary>
    public void ReturnVolumeBeforeNextProbe(char driveLetter) =>
        Volatile.Write(ref _beforeVolumeFactsCaptured, () => SetVolumeAbsent(driveLetter, absent: false));

    /// <summary>
    ///     From now on every volume facts read flips the volume's presence before capturing it, so
    ///     successive probes read ready, absent, ready, and so on: a flapping volume.
    /// </summary>
    public void FlapVolumeOnEveryProbe(char driveLetter) => Volatile.Write(ref _flappingProbes, char.ToUpperInvariant(driveLetter));

    /// <summary>
    ///     Inside the next volume facts read, after the facts are captured and before they are
    ///     returned, makes the volume present and raises its arrival: the volume comes back behind
    ///     the back of a probe that still reports it absent.
    /// </summary>
    public void ReturnVolumeDuringNextProbe(char driveLetter) => Volatile.Write(ref _afterVolumeFactsCaptured, () =>
    {
        SetVolumeAbsent(driveLetter, absent: false);
        RaiseVolumeArrival();
    });

    /// <summary>Every line the indices reported to <see cref="FileIndexOptions.Diagnostics" />.</summary>
    public IReadOnlyCollection<string> Diagnostics => _diagnostics;

    /// <summary>The clock the harness's indices take their retry pauses from.</summary>
    public RetryClock Clock { get; } = new();

    /// <summary>The harness's own arrival registration, counting how many times the indices hold it.</summary>
    public CountingArrivalRegistration ArrivalRegistration { get; } = new();

    VolumeArrivalSignal? _arrivals;

    VolumeArrivalSignal Arrivals => _arrivals ??= new VolumeArrivalSignal(ArrivalRegistration.Register);

    /// <summary>
    ///     Delivers one volume arrival to every index, as the operating system's notification would,
    ///     and records a recovery an arrival revives so <see cref="WaitForRecoveryAsync" /> follows it.
    /// </summary>
    public void RaiseVolumeArrival()
    {
        foreach (var index in _indicesByDrive.Values)
        {
            index.WatchStateChanged += RecordRevivedRecovery;
        }

        try
        {
            Arrivals.Raise();
        }
        finally
        {
            foreach (var index in _indicesByDrive.Values)
            {
                index.WatchStateChanged -= RecordRevivedRecovery;
            }
        }
    }

    void RecordRevivedRecovery(WatchStateChange change)
    {
        if (change.Watch.State != WatchState.Recovering ||
            !IndexFor(change.Volume.Letter).TryGetRecoveryCompletionForTest(change.Volume, out var recovery))
        {
            return;
        }

        lock (_faults)
        {
            var recoveries = _recoveriesByDrive.GetValueOrDefault(change.Volume.Letter) ?? [];
            _recoveriesByDrive[change.Volume.Letter] = recoveries;
            if (!recoveries.Contains(recovery))
            {
                recoveries.Add(recovery);
            }
        }
    }

    /// <summary>
    ///     While absent, the root's volume facts read not ready and every scan of it fails, as for a
    ///     volume that was detached.
    /// </summary>
    public void SetVolumeAbsent(char driveLetter, bool absent) =>
        _absentDrives[char.ToUpperInvariant(driveLetter)] = absent;

    FileIndexOptions WithClockAndVolumeAbsence(FileIndexOptions options, char driveLetter)
    {
        var present = options.VolumeFacts ?? throw new InvalidOperationException("The synthetic root supplies volume facts.");
        return options with
        {
            TimeProvider = Clock,
            VolumeArrivals = Arrivals,
            Diagnostics = _diagnostics.Enqueue,
            VolumeFacts = (root, directory) =>
            {
                Interlocked.Exchange(ref _beforeVolumeFactsCaptured, null)?.Invoke();
                if (Volatile.Read(ref _flappingProbes) == char.ToUpperInvariant(driveLetter))
                {
                    SetVolumeAbsent(driveLetter, !IsAbsent(driveLetter));
                }

                var facts = present(root, directory) with { IsReady = !IsAbsent(driveLetter) };
                _volumeFactsReads.Writer.TryWrite(char.ToUpperInvariant(driveLetter));
                Interlocked.Exchange(ref _afterVolumeFactsCaptured, null)?.Invoke();
                return facts;
            }
        };
    }

    /// <summary>Forgets the volume facts reads so far, so <see cref="WaitForVolumeFactsReadAsync" /> waits for the next one.</summary>
    public void ForgetVolumeFactsReads()
    {
        while (_volumeFactsReads.Reader.TryRead(out _))
        {
        }
    }

    /// <summary>Completes once any root's volume facts have been read again, as a recovery's probe of its volume reads them.</summary>
    public async Task WaitForVolumeFactsReadAsync(CancellationToken cancellationToken) =>
        _ = await _volumeFactsReads.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(HangGuard, cancellationToken);

    bool IsAbsent(char driveLetter) => _absentDrives.GetValueOrDefault(char.ToUpperInvariant(driveLetter));

    /// <summary>Stands in for the platform registration: counts registrations and how many are still held.</summary>
    internal sealed class CountingArrivalRegistration
    {
        int _registrations;
        int _held;

        /// <summary>How many times the signal registered with the platform.</summary>
        public int Registrations => Volatile.Read(ref _registrations);

        /// <summary>Whether the signal holds a registration now.</summary>
        public bool IsHeld => Volatile.Read(ref _held) != 0;

        /// <summary>When set, every registration fails with it, as a platform that refuses notifications would.</summary>
        public Exception? Failure { get; set; }

        public IDisposable Register(Action onArrival)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            Interlocked.Increment(ref _registrations);
            Interlocked.Exchange(ref _held, 1);
            return new Release(this);
        }

        sealed class Release(CountingArrivalRegistration owner) : IDisposable
        {
            public void Dispose() => Interlocked.Exchange(ref owner._held, 0);
        }
    }

    /// <summary>A fake clock that counts the timers the index creates, so a test advances time only once a pause has begun.</summary>
    internal sealed class RetryClock : Microsoft.Extensions.Time.Testing.FakeTimeProvider
    {
        readonly Channel<bool> _timersCreated = Channel.CreateUnbounded<bool>();
        int _pausesBegun;

        /// <summary>How many timers the index has created on this clock.</summary>
        public int PausesBegun => Volatile.Read(ref _pausesBegun);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _pausesBegun);
            _timersCreated.Writer.TryWrite(true);
            return timer;
        }

        /// <summary>Completes once the index has begun its next pause; each call consumes one timer.</summary>
        public async Task WaitForPauseAsync(CancellationToken cancellationToken) =>
            _ = await _timersCreated.Reader.ReadAsync(cancellationToken);
    }
}
