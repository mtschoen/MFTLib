using System.Runtime.CompilerServices;

namespace MFTLib;

// Live-watch half of the broker client: after the cold scan, a single background
// reader owns the pipe and demultiplexes JournalBatch frames into per-drive channels.
// A single subscriber per drive (e.g. a journal watcher) calls CreateBatchSource once
// per drive, so this single-reader demux is what stops the per-drive subscribers from
// racing on the shared pipe.
public sealed partial class JournalBrokerClient
{
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
        ThrowIfControlUnavailable();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _controlCancellation.Token);
        var operationToken = operationCancellation.Token;
        await _armOrderingGate.WaitAsync(operationToken).ConfigureAwait(false);
        var armEpochsByDrive = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            ThrowIfControlUnavailable();
            string watchSpec;
            uint generation;
            lock (_liveChannelsLock)
            {
                if (!_liveWatchGenerationStarted)
                {
                    if (_lastWatchGeneration == uint.MaxValue)
                    {
                        throw new InvalidOperationException("Watch generation counter has reached maximum value.");
                    }

                    _lastWatchGeneration++;
                    _activeWatchGeneration = _lastWatchGeneration;
                }

                generation = _activeWatchGeneration;
                watchSpec = ArmAndFormatWatchSpecLocked(cursorsByDrive, armEpochsByDrive);
            }

            await WriteFrameAsync(
                writer => BrokerProtocol.WriteStartWatch(writer, watchSpec, generation),
                transmissionStarted, operationToken).ConfigureAwait(false);

            lock (_liveChannelsLock)
            {
                EnsureLiveDemuxStartedLocked(generation, cancellationToken, operationToken);
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposeStarted) != 0 && !cancellationToken.IsCancellationRequested)
        {
            DisarmDrivesLocked(armEpochsByDrive.Keys);
            ThrowIfControlUnavailable();
            throw;
        }
        catch
        {
            DisarmDrivesLocked(armEpochsByDrive.Keys);
            throw;
        }
        finally
        {
            _armOrderingGate.Release();
        }
    }

    string ArmAndFormatWatchSpecLocked(
        IReadOnlyDictionary<string, UsnJournalCursor> cursorsByDrive,
        Dictionary<string, uint> armEpochsByDrive)
    {
        ValidateClaimedGenerationLocked();
        var normalizedCursors = cursorsByDrive
            .Select(pair => new KeyValuePair<string, UsnJournalCursor>(
                NormalizeDriveLetter(pair.Key), pair.Value))
            .ToArray();
        foreach (var pair in normalizedCursors)
        {
            armEpochsByDrive[pair.Key] = ArmDriveLocked(pair.Key);
        }
        var specTokens = normalizedCursors.Select(pair => FormattableString.Invariant(
            $"{pair.Key}:{pair.Value.JournalId}:{pair.Value.NextUsn}:{armEpochsByDrive[pair.Key]}"));
        return string.Join(",", specTokens);
    }

    void EnsureLiveDemuxStartedLocked(uint generation, CancellationToken cancellationToken, CancellationToken operationToken)
    {
        if (_liveWatchGenerationStarted)
        {
            ValidateClaimedGenerationLocked();
            return;
        }

        operationToken.ThrowIfCancellationRequested();

        var demuxCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _controlCancellation.Token);
        var demuxToken = demuxCancellation.Token;
        _demuxCts = demuxCancellation;
        _demuxTask = Task.Run(() => DemuxLoopAsync(generation, demuxToken), CancellationToken.None);
        _liveWatchGenerationStarted = true;
    }

    void DisarmDrivesLocked(IEnumerable<string> drives)
    {
        lock (_liveChannelsLock)
        {
            foreach (var drive in drives)
            {
                DisarmDriveLocked(drive);
            }
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
        ThrowIfControlUnavailable();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _controlCancellation.Token);
        var operationToken = operationCancellation.Token;
        await _armOrderingGate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            ThrowIfControlUnavailable();
            lock (_liveChannelsLock)
            {
                // Complete the subscriber without waiting for a wire round trip.
                DisarmDriveLocked(normalizedDrive);
            }

            await WriteFrameAsync(writer => BrokerProtocol.WriteDisarmDrive(writer, normalizedDrive),
                operationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposeStarted) != 0 && !cancellationToken.IsCancellationRequested)
        {
            ThrowIfControlUnavailable();
            throw;
        }
        finally
        {
            _armOrderingGate.Release();
        }
    }

    /// <summary>
    ///     Stop the live-watch demux and reset live-watch state so the same client can watch
    ///     again (keeping the broker process - and its elevation - alive).
    ///     When a live demux is running it owns the pipe and routes scan/query replies
    ///     to the active serialized control exchange. With no live demux, the control
    ///     exchange owns the foreground reader. The ordering gate fences that handoff.
    ///     No-op if no watch is running. Does NOT signal broker death: a clean stop leaves the client
    ///     healthy for restart.
    /// </summary>
    public async Task StopLiveWatchAsync(CancellationToken cancellationToken = default)
    {
        await _armOrderingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                return;
            }
            if (Volatile.Read(ref _controlFailure) != null)
            {
                Task? demux;
                lock (_liveChannelsLock)
                {
                    demux = _demuxTask;
                }
                if (demux != null)
                {
                    await demux.ConfigureAwait(false);
                }
                // The normal core joins and disposes the ended demux's cancellation source.
                await StopLiveWatchCoreAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            await StopLiveWatchCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _armOrderingGate.Release();
        }
    }

    async Task StopLiveWatchCoreAsync(CancellationToken cancellationToken)
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
        if (Volatile.Read(ref _controlFailure) == null)
        {
            try
            {
                await WriteFrameAsync(BrokerProtocol.WriteEndWatch, _controlCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _disposeStarted) != 0)
            {
                // Disposal has already cancelled the demux; the join below releases reader ownership.
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Swallowed intentionally: the pipe may already be gone, in which case
                // the demux ends via EOF. Fall through to await it either way.
                _ = exception;
            }
        }

        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await demuxCancellation.CancelAsync().ConfigureAwait(false);
            await task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        finally
        {
            lock (_liveChannelsLock)
            {
                _demuxCts = null;
                _demuxTask = null;
            }
            demuxCancellation.Dispose();

            ResetLiveWatchState();
        }
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
            // Keep _lastArmEpoch: old frames can remain on the pipe.
            // Keep _lastWatchGeneration: monotonic across stops.
            _activeWatchGeneration = 0;
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
            await foreach (var item in ReadItemsAsync(driveLetter, since, cancellationToken).ConfigureAwait(false))
            {
                if (item is LiveWatchItem.Batch batch)
                {
                    yield return (batch.Entries, batch.Cursor);
                }
            }
        }
    }

    /// <summary>
    ///     Returns the item-level counterpart to <see cref="CreateBatchSource" />, carrying the
    ///     drive's catch-up markers alongside its batches. Internal: the only consumer is
    ///     <see cref="BrokerIndexWatchSource" />.
    /// </summary>
    internal LiveWatchItemSource CreateLiveWatchItemSource()
    {
        return ReadItemsAsync;
    }

    async IAsyncEnumerable<LiveWatchItem> ReadItemsAsync(
        string driveLetter, UsnJournalCursor since,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = GetOrAddLiveChannel(NormalizeDriveLetter(driveLetter));
        // ReadAllAsync completes normally on Channel.Complete() and throws the
        // demux's InvalidOperationException on Channel.Complete(error) (broker death).
        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }
}
