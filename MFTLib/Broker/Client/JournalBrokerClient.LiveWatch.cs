using System.Runtime.CompilerServices;

namespace MFTLib;

// Live-watch half of the broker client: after the cold scan, a single background
// reader owns the pipe and demultiplexes JournalBatch frames into per-drive channels.
// A single subscriber per drive (e.g. a journal watcher) calls CreateBatchSource once
// per drive, so this single-reader demux is what stops the per-drive subscribers from
// racing on the shared pipe.
public sealed partial class JournalBrokerClient
{
    // How long StopLiveWatchAsync waits for the host's EndWatchAck before forcing
    // the demux down (a wedged or dead broker that never replies). Internal and
    // mutable (rather than a readonly constant) so tests can shrink the window
    // instead of sleeping for the real production timeout.
    internal static TimeSpan _endWatchAckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Indicates whether the most recent call to <see cref="StopLiveWatchAsync" /> timed out
    ///     waiting for the host's <c>EndWatchAck</c> response and forced the demux loop down via
    ///     cancellation, rather than completing via the normal ack handshake.
    /// </summary>
    internal bool LastStopTimedOut { get; private set; }

    CancellationTokenSource? _demuxCts;
    Task? _demuxTask;

    /// <summary>
    ///     Arm every drive named by its per-drive resume cursor. The first call starts the
    ///     live-watch generation and its single pipe-reading demux. A later call re-arms any
    ///     already-armed drive by replacing that drive's channel, while leaving drives the
    ///     call does not name alone.
    ///     Each named drive receives a fresh arm epoch. Frames the broker already wrote
    ///     for an earlier arm are discarded, so re-arming from a fresh cursor cannot
    ///     deliver a batch produced before that arm.
    /// </summary>
    public Task SendStartWatchAsync(
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,
        CancellationToken cancellationToken = default)
    {
        return SendStartWatchCoreAsync(cursorsByDrive, null, cancellationToken);
    }

    internal Task SendStartWatchAsync(
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,
        Action transmissionStarted,
        CancellationToken cancellationToken)
    {
        return SendStartWatchCoreAsync(cursorsByDrive, transmissionStarted, cancellationToken);
    }

    async Task SendStartWatchCoreAsync(
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,
        Action? transmissionStarted,
        CancellationToken cancellationToken)
    {
        await _armOrderingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var armEpochsByDrive = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var normalizedCursors = cursorsByDrive
                .Select(pair => new KeyValuePair<string, UsnJournalCursor>(
                    NormalizeDriveLetter(pair.Key), pair.Value))
                .ToArray();
            lock (_liveChannelsLock)
            {
                ValidateClaimedGenerationLocked();
                foreach (var pair in normalizedCursors)
                {
                    armEpochsByDrive[pair.Key] = ArmDriveLocked(pair.Key);
                }
            }

            // Watch spec tokens are four fields: letter:journalId:nextUsn:armEpoch.
            var specTokens = normalizedCursors.Select(pair =>
            {
                return FormattableString.Invariant(
                    $"{pair.Key}:{pair.Value.JournalId}:{pair.Value.NextUsn}:{armEpochsByDrive[pair.Key]}");
            });
            var watchSpec = string.Join(",", specTokens);

            await WriteFrameAsync(
                writer => BrokerProtocol.WriteStartWatch(writer, watchSpec),
                transmissionStarted, cancellationToken).ConfigureAwait(false);

            lock (_liveChannelsLock)
            {
                if (_liveWatchGenerationStarted)
                {
                    ValidateClaimedGenerationLocked();
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Install the cancellation source, reader, and generation claim together.
                // After a failed start or any stop, the next start either installs or reuses
                // a live demux, or fails loudly; success without a reader is impossible.
                var demuxCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Capture the token before Task.Run because a racing stop can dispose the
                // source before the delegate starts; the captured token remains usable.
                var demuxToken = demuxCancellation.Token;
                _demuxCts = demuxCancellation;
                _demuxTask = Task.Run(() => DemuxLoopAsync(demuxToken), CancellationToken.None);
                _liveWatchGenerationStarted = true;
            }
        }
        catch
        {
            lock (_liveChannelsLock)
            {
                foreach (var drive in armEpochsByDrive.Keys)
                {
                    DisarmDriveLocked(drive);
                }
            }
            throw;
        }
        finally
        {
            _armOrderingGate.Release();
        }
    }

    void ValidateClaimedGenerationLocked()
    {
        if (!_liveWatchGenerationStarted)
        {
            return;
        }

        if (_demuxCts == null || _demuxTask == null || _demuxCts.IsCancellationRequested || _liveEnded)
        {
            throw new InvalidOperationException(
                "The live watch reader has ended or is inconsistent. Call StopLiveWatchAsync before starting again.");
        }
    }

    /// <summary>
    ///     Retire one drive from the live watch generation, leaving every other drive
    ///     streaming. That drive's channel is completed normally, so its subscriber's
    ///     enumeration ends rather than throwing, and any batch received while the drive
    ///     remains disarmed is dropped by the demux. Arming it again with
    ///     <see cref="SendStartWatchAsync(IReadOnlyDictionary{string,UsnJournalCursor},CancellationToken)" />
    ///     gives it a fresh channel and a fresh subscriber.
    /// </summary>
    public Task SendDisarmDriveAsync(string driveLetter, CancellationToken cancellationToken = default)
    {
        var normalizedDrive = NormalizeDriveLetter(driveLetter);
        lock (_liveChannelsLock)
        {
            if (!_liveWatchGenerationStarted)
            {
                throw new InvalidOperationException(
                    "No live watch is running for this client, so there is nothing to disarm.");
            }
        }

        return SendDisarmDriveCoreAsync(normalizedDrive, cancellationToken);
    }

    async Task SendDisarmDriveCoreAsync(string normalizedDrive, CancellationToken cancellationToken)
    {
        // Keep the local retirement and wire write in the same order as concurrent arms.
        await _armOrderingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_liveChannelsLock)
            {
                // Complete the subscriber without waiting for a wire round trip.
                DisarmDriveLocked(normalizedDrive);
            }

            await WriteFrameAsync(writer => BrokerProtocol.WriteDisarmDrive(writer, normalizedDrive),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _armOrderingGate.Release();
        }
    }

    /// <summary>
    ///     Stop the live-watch demux and reset live-watch state so the same client can watch
    ///     again (used by a rescan, which must reclaim the pipe as the arm-and-scan's sole
    ///     reader while keeping the broker process - and its elevation - alive). No-op if no
    ///     watch is running. Does NOT signal broker death: a clean stop leaves the client
    ///     healthy for restart.
    /// </summary>
    public async Task StopLiveWatchAsync()
    {
        CancellationTokenSource? demuxCancellation;
        Task? task;
        lock (_liveChannelsLock)
        {
            task = _demuxTask;
            demuxCancellation = _demuxCts;
        }

        if (task == null)
        {
            ResetLiveWatchState();
            return;
        }

        // The reader and cancellation source are published and captured together.
        if (demuxCancellation == null)
        {
            throw new InvalidOperationException(
                "Live-watch state is inconsistent: _demuxTask is set but _demuxCts is not.");
        }

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

        using (var timeout = new CancellationTokenSource(_endWatchAckTimeout))
        {
            // Task.Delay faults with TaskCanceledException when the timeout fires, but
            // Task.WhenAny never throws, so reading the winner is safe.
            var finished = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, timeout.Token))
                .ConfigureAwait(false);
            if (finished != task)
            // No ack within the window (broker wedged): force the demux down.
            {
                LastStopTimedOut = true;
                await demuxCancellation.CancelAsync().ConfigureAwait(false);
            }
            else
            {
                LastStopTimedOut = false;
            }
        }

        // DemuxLoopAsync catches everything internally and never lets an exception
        // escape, so awaiting it here cannot fault.
        await task.ConfigureAwait(false);

        lock (_liveChannelsLock)
        {
            _demuxCts = null;
            _demuxTask = null;
        }
        demuxCancellation.Dispose();

        ResetLiveWatchState();
    }

    void ResetLiveWatchState()
    {
        lock (_liveChannelsLock)
        {
            foreach (var channel in _liveChannels.Values)
            {
                channel.Writer.TryComplete();
            }

            _liveChannels.Clear();
            _armedEpochsByDrive.Clear();
            // Keep _lastArmEpoch: a timed-out stop can leave old frames on the pipe.
            _liveEnded = false;
            _liveEndError = null;
            // Release the generation so a rescan can begin a fresh watch on this client.
            _liveWatchGenerationStarted = false;
        }
    }

    /// <summary>
    ///     Returns a <see cref="JournalBatchSource" /> delegate that yields live
    ///     <see cref="BrokerFrameKind.JournalBatch" /> frames for a single drive, reading
    ///     from the channel the demux loop fills. Throws
    ///     <see cref="InvalidOperationException" /> when the broker dies so the watcher flips
    ///     that drive inactive. Public so consumers in other assemblies can feed it into
    ///     their own journal-watching loop.
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
            {
                yield return batch;
            }
        }
    }
}
