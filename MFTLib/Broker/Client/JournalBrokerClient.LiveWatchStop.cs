namespace MFTLib;

// Stopping the live watch. Every watch this client starts carries a client-wide generation
// number that its StartWatch, EndWatch, and EndWatchAck frames all name, and a demux ends only
// on the acknowledgement for its own generation. A stop can therefore stop waiting for the
// acknowledgement without leaving it on the pipe to end whichever watch this client starts next.
public sealed partial class JournalBrokerClient
{
    // Client-wide and never reset, so no two watch generations on one connection share a number.
    uint _lastWatchGeneration;

    // The generation of the live watch while one is started. Guarded by _liveChannelsLock.
    uint _liveWatchGeneration;

    // Set when a stop was cancelled while it waited for the arm-ordering gate. The next holder of
    // the gate tears the watch down before doing anything else. Guarded by _liveChannelsLock.
    bool _liveWatchStopDeferred;

    /// <summary>
    ///     Stop the live-watch demux and reset live-watch state so the same client can watch
    ///     again (keeping the broker process - and its elevation - alive).
    ///     When a live demux is running it owns the pipe and routes scan/query replies
    ///     to the active serialized control exchange. With no live demux, the control
    ///     exchange owns the foreground reader. The ordering gate fences that handoff.
    ///     No-op if no watch is running. Does NOT signal broker death: a clean stop leaves the client
    ///     healthy for restart.
    ///     <para>
    ///         The stop sends <c>EndWatch</c> for the current watch generation and waits for the
    ///         demux to end, on the broker's <c>EndWatchAck</c> for that generation or on the pipe
    ///         closing, which is how a dead broker ends it without any token. Nothing else bounds
    ///         the wait: a live broker that never acknowledges is bounded only by
    ///         <paramref name="cancellationToken" />, and by disposing the client.
    ///     </para>
    ///     <para>
    ///         Cancelling <paramref name="cancellationToken" /> throws
    ///         <see cref="OperationCanceledException" /> once the client has stopped reading the old
    ///         watch and reset its live-watch state, or, when the cancellation arrives while another
    ///         operation holds the client's arm-ordering gate, once that teardown is scheduled for
    ///         the next operation to take the gate. Either way the client stays usable for another
    ///         watch with no reconnect: the next watch carries a later generation, its demux ignores
    ///         the old generation's acknowledgement whenever it arrives, and the broker stops the old
    ///         generation before it arms the new one. The <c>EndWatch</c> frame itself is never cut
    ///         off mid-write, so a cancelled stop cannot leave half a frame on the pipe.
    ///     </para>
    /// </summary>
    public async Task StopLiveWatchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _armOrderingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeferLiveWatchStop();
            throw;
        }

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
                    // A control failure cancels the demux, so this join cannot wait on the broker.
                    await demux.ConfigureAwait(false);
                }
            }

            if (!await StopLiveWatchCoreAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new OperationCanceledException(
                    "The live watch stopped without waiting for the broker's EndWatchAck.", null, cancellationToken);
            }
        }
        finally
        {
            _armOrderingGate.Release();
        }
    }

    void DeferLiveWatchStop()
    {
        lock (_liveChannelsLock)
        {
            if (_liveWatchGenerationStarted)
            {
                _liveWatchStopDeferred = true;
            }
        }
    }

    /// <summary>
    ///     Runs the teardown of a stop that was cancelled while it waited for the arm-ordering gate.
    ///     Every operation that takes the gate calls this first, while it holds the gate, so the old
    ///     watch is gone before that operation writes a frame or reads the pipe.
    /// </summary>
    async Task CompleteDeferredLiveWatchStopAsync()
    {
        bool deferred;
        lock (_liveChannelsLock)
        {
            deferred = _liveWatchStopDeferred;
        }

        if (deferred)
        {
            await StopLiveWatchCoreAsync(new CancellationToken(canceled: true)).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Ends the current generation and resets live-watch state; the caller holds the
    ///     arm-ordering gate. Returns false when <paramref name="cancellationToken" /> ended the wait
    ///     for the demux before the broker acknowledged or the pipe closed.
    /// </summary>
    async Task<bool> StopLiveWatchCoreAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? demuxCancellation;
        Task? task;
        uint watchGeneration;
        lock (_liveChannelsLock)
        {
            task = _demuxTask;
            demuxCancellation = _demuxCts;
            watchGeneration = _liveWatchGeneration;
            _liveWatchStopDeferred = false;
        }

        if (task == null)
        {
            ResetLiveWatchState();
            return true;
        }

        // The reader and cancellation source are published and captured together.
        if (demuxCancellation == null)
        {
            throw new InvalidOperationException(
                "Live-watch state is inconsistent: _demuxTask is set but _demuxCts is not.");
        }

        // Ask the host to end the watch; the demux exits when it reads this generation's
        // EndWatchAck (draining any stray live batches in between) or on EOF if the broker is
        // already dead. The send is not awaited: this stop's token must not cut the frame off
        // mid-write, and a broker that stopped reading the pipe must not hold the stop past it.
        if (Volatile.Read(ref _controlFailure) == null)
        {
            _ = SendEndWatchAsync(watchGeneration);
        }

        var ended = true;
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ended = false;
            await demuxCancellation.CancelAsync().ConfigureAwait(false);
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
        return ended;
    }

    /// <summary>
    ///     Writes one <c>EndWatch</c> frame whole. Only disposal of the client interrupts it, and
    ///     disposal closes the pipe anyway. A failed write is not reported here: a pipe that is gone
    ///     ends the demux through EOF or a read fault, which is what the stop waits on.
    /// </summary>
    async Task SendEndWatchAsync(uint watchGeneration)
    {
        try
        {
            await WriteFrameAsync(writer => BrokerProtocol.WriteEndWatch(writer, watchGeneration),
                _controlCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Deliberate broad catch: the pipe may be broken, closed, or disposed mid-frame, and
            // the demux observes each of those itself. Discarded through the variable, this
            // repository's idiom for a deliberate swallow.
            _ = exception;
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
            // Keep _lastArmEpoch and _lastWatchGeneration: a cancelled stop can leave the old
            // generation's frames, and its acknowledgement, on the pipe.
            _liveEnded = false;
            _liveEndError = null;
            _liveWatchStopDeferred = false;
            // Release the generation so a rescan can begin a fresh watch on this client.
            _liveWatchGenerationStarted = false;
        }
    }
}
