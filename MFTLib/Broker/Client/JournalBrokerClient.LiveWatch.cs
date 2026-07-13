using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MFTLib;

// Live-watch half of the broker client: after the cold scan, a single background
// reader owns the pipe and demultiplexes JournalBatch frames into per-drive channels.
// A single subscriber per drive (e.g. a journal watcher) calls CreateBatchSource once
// per drive, so this single-reader demux is what stops the per-drive subscribers from
// racing on the shared pipe.
public sealed partial class JournalBrokerClient
{
    readonly object _liveChannelsLock = new();
    readonly Dictionary<string, Channel<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>> _liveChannels =
        new(StringComparer.OrdinalIgnoreCase);
    Task? _demuxTask;
    CancellationTokenSource? _demuxCts;
    // Atomically reserves the single live-watch start. The _demuxTask null-check alone was
    // not atomic with its assignment, so two concurrent SendStartWatchAsync callers could
    // both pass it and both start a demux loop - two readers racing one pipe corrupt frames.
    int _watchStartGuard;
    // How long StopLiveWatchAsync waits for the host's EndWatchAck before forcing
    // the demux down (a wedged or dead broker that never replies). Internal and
    // mutable (rather than a readonly constant) so tests can shrink the window
    // instead of sleeping for the real production timeout.
    internal static TimeSpan EndWatchAckTimeout = TimeSpan.FromSeconds(5);
    // Latch the demux's terminal state so a drive that subscribes AFTER the broker
    // died still gets an already-completed channel instead of blocking forever.
    bool _liveEnded;
    Exception? _liveEndError;

    /// <summary>
    /// Send a <c>StartWatch</c> frame for the given per-drive resume cursors and begin
    /// the live-watch demux: a single background reader takes ownership of the pipe and
    /// routes each incoming <see cref="BrokerFrameKind.JournalBatch"/> frame into its
    /// drive's channel. Call this exactly once, after
    /// <see cref="ArmScanAndCatchUpAsync"/> has drained the cold-scan frames; the
    /// per-drive delegates from <see cref="CreateBatchSource"/> then read those channels.
    /// </summary>
    public async Task SendStartWatchAsync(
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,
        CancellationToken cancellationToken = default)
    {
        // Reserve the start atomically up front: two concurrent callers (a double
        // scan-complete tick) must not both send StartWatch + start a demux loop.
        if (Interlocked.CompareExchange(ref _watchStartGuard, 1, 0) != 0)
            throw new InvalidOperationException("Live watch has already been started for this client");

        // Watch spec tokens omit the map name (three fields): letter:journalId:nextUsn.
        var specTokens = cursorsByDrive.Select(pair => FormattableString.Invariant(
            $"{NormalizeDriveLetter(pair.Key)}:{pair.Value.JournalId}:{pair.Value.NextUsn}"));
        var watchSpec = string.Join(",", specTokens);

        await WriteFrameAsync(
            writer => BrokerProtocol.WriteStartWatch(writer, watchSpec), cancellationToken)
            .ConfigureAwait(false);

        // Own a CTS for the demux so DisposeAsync can cancel the blocking pipe read
        // (disposing the pipe alone does not reliably unblock a pending ReadAsync).
        // Read .Token now, on this thread, and close over that value rather than the
        // CTS: Task.Run's lambda runs on a thread-pool thread whose start can be
        // delayed, and by the time it runs the CTS could already be disposed by a
        // racing StopLiveWatchAsync/DisposeAsync - CancellationTokenSource.Token
        // throws ObjectDisposedException in that case, whereas the CancellationToken
        // value itself stays valid (and already reflects any Cancel() that happened
        // before the dispose) after its source is gone.
        var demuxCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var demuxToken = demuxCts.Token;
        _demuxCts = demuxCts;
        _demuxTask = Task.Run(() => DemuxLoopAsync(demuxToken), CancellationToken.None);
    }

    /// <summary>
    /// Stop the live-watch demux and reset live-watch state so the same client can watch
    /// again (used by a rescan, which must reclaim the pipe as the arm-and-scan's sole
    /// reader while keeping the broker process - and its elevation - alive). No-op if no
    /// watch is running. Does NOT signal broker death: a clean stop leaves the client
    /// healthy for restart.
    /// </summary>
    public async Task StopLiveWatchAsync()
    {
        var task = _demuxTask;
        if (task == null)
            return; // not watching

        // _demuxCts is always set alongside _demuxTask in SendStartWatchAsync and only
        // cleared here, together, at the end of a stop - so it must be non-null now.
        var demuxCts = _demuxCts
            ?? throw new InvalidOperationException("Live-watch state is inconsistent: _demuxTask is set but _demuxCts is not.");

        // Ask the host to end the watch; the demux exits when it reads EndWatchAck
        // (draining any stray live batches in between) or on EOF if the broker is
        // already dead.
        try
        {
            await WriteFrameAsync(BrokerProtocol.WriteEndWatch, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Swallowed intentionally: the pipe may already be gone, in which case
            // the demux ends via EOF. Fall through to await it either way.
            _ = exception;
        }

        using (var timeout = new CancellationTokenSource(EndWatchAckTimeout))
        {
            // Task.Delay faults with TaskCanceledException when the timeout fires, but
            // Task.WhenAny never throws, so reading the winner is safe.
            var finished = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, timeout.Token))
                .ConfigureAwait(false);
            if (finished != task)
                // No ack within the window (broker wedged): force the demux down.
                await demuxCts.CancelAsync().ConfigureAwait(false);
        }

        // DemuxLoopAsync catches everything internally and never lets an exception
        // escape, so awaiting it here cannot fault.
        await task.ConfigureAwait(false);

        demuxCts.Dispose();
        _demuxCts = null;
        _demuxTask = null;
        // Release the start reservation so a rescan can begin a fresh watch on this client.
        Interlocked.Exchange(ref _watchStartGuard, 0);

        lock (_liveChannelsLock)
        {
            _liveChannels.Clear();
            _liveEnded = false;
            _liveEndError = null;
        }
    }

    /// <summary>
    /// Returns a <see cref="JournalBatchSource"/> delegate that yields live
    /// <see cref="BrokerFrameKind.JournalBatch"/> frames for a single drive, reading
    /// from the channel the demux loop fills. Throws
    /// <see cref="InvalidOperationException"/> when the broker dies so the watcher flips
    /// that drive inactive. Public so consumers in other assemblies can feed it into
    /// their own journal-watching loop.
    /// </summary>
    public JournalBatchSource CreateBatchSource()
    {
        return ReadBatchesAsync;

        async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> ReadBatchesAsync(
            string driveLetter, UsnJournalCursor since,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var channel = GetOrAddLiveChannel(NormalizeDriveLetter(driveLetter));
            // ReadAllAsync completes normally on Channel.Complete() and throws the
            // demux's InvalidOperationException on Channel.Complete(error) (broker death).
            await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return batch;
        }
    }

    // Single owner of the pipe during live watch: read frames and route each
    // JournalBatch to its drive's channel until the broker dies or is cancelled.
    async Task DemuxLoopAsync(CancellationToken cancellationToken)
    {
        BrokerDiagnostics.Log($"DemuxLoopAsync started (t={Environment.CurrentManagedThreadId}).");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame == null)
                {
                    // SignalBrokerDeath was already called inside ReadFrameAsync on EOF.
                    CompleteAllLiveChannels(new InvalidOperationException("Broker pipe closed: Pipe EOF"));
                    return;
                }

                var value = frame.Value;
                if (value.Kind == BrokerFrameKind.JournalBatch)
                    GetOrAddLiveChannel(NormalizeDriveLetter(value.RequireDrive()))
                        .Writer.TryWrite((value.Entries, value.Cursor));
                else if (value.Kind == BrokerFrameKind.EndWatchAck)
                {
                    CompleteAllLiveChannels(error: null);
                    return; // clean stop: the watch was ended at the client's request
                }
                // Heartbeat / Error / other frame kinds are not routed to a drive stream.
            }

            // The loop can also exit because cancellation was observed at the top of
            // an iteration, rather than by an already-blocked read throwing below -
            // complete the channels the same way the OperationCanceledException catch
            // does, so a subscriber's await-foreach ends instead of hanging forever.
            CompleteAllLiveChannels(error: null);
        }
        // Deliberate broad catch: any IO or protocol error on the pipe is broker death;
        // the watcher subscribers must see it as a fault. Cancellation ends quietly.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SignalBrokerDeath(exception.Message);
            CompleteAllLiveChannels(new InvalidOperationException($"Broker pipe closed: {exception.Message}"));
        }
        catch (OperationCanceledException)
        {
            CompleteAllLiveChannels(error: null);
        }
    }

    Channel<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> GetOrAddLiveChannel(string normalizedDrive)
    {
        lock (_liveChannelsLock)
        {
            if (!_liveChannels.TryGetValue(normalizedDrive, out var channel))
            {
                channel = Channel.CreateUnbounded<(UsnJournalEntry[], UsnJournalCursor)>();
                // If the broker already died, hand back an already-completed channel so
                // a late subscriber faults immediately rather than awaiting forever.
                if (_liveEnded)
                    channel.Writer.TryComplete(_liveEndError);
                _liveChannels[normalizedDrive] = channel;
            }
            return channel;
        }
    }

    void CompleteAllLiveChannels(Exception? error)
    {
        lock (_liveChannelsLock)
        {
            _liveEnded = true;
            _liveEndError = error;
            foreach (var channel in _liveChannels.Values)
                channel.Writer.TryComplete(error);
        }
    }
}
