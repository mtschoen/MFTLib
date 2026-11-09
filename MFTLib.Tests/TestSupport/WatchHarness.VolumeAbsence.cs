using System.Collections.Concurrent;
using System.Threading.Channels;
using MFTLib.Index;

namespace MFTLib.Tests.TestSupport;

/// <summary>
///     A volume that can be taken away and returned, and the fake clock the index waits on while
///     its automatic recovery retries.
/// </summary>
sealed partial class WatchHarness
{
    readonly ConcurrentDictionary<char, bool> _absentDrives = [];

    /// <summary>The clock the harness's indices take their retry pauses from.</summary>
    public RetryClock Clock { get; } = new();

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
            VolumeFacts = (root, directory) =>
                present(root, directory) with { IsReady = !IsAbsent(driveLetter) }
        };
    }

    bool IsAbsent(char driveLetter) => _absentDrives.GetValueOrDefault(char.ToUpperInvariant(driveLetter));

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
