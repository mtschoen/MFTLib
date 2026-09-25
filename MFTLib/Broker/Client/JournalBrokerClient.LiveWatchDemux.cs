using System.Threading.Channels;

namespace MFTLib;

public sealed partial class JournalBrokerClient
{
    readonly Dictionary<string, Channel<LiveWatchItem>> _liveChannels =
        new(StringComparer.OrdinalIgnoreCase);

    readonly object _liveChannelsLock = new();

    // Deliver live batches and errors only when their epoch matches the drive's current arm.
    readonly Dictionary<string, uint> _armedEpochsByDrive = new(StringComparer.OrdinalIgnoreCase);

    // Client-wide and never reset: a cancelled stop can leave old frames unread on the pipe.
    uint _lastArmEpoch;

    // Guarded by _liveChannelsLock so only the first arm starts the pipe reader.
    bool _liveWatchGenerationStarted;

    Exception? _liveEndError;

    // Latch the demux's terminal state so a drive that subscribes AFTER the broker
    // died still gets an already-completed channel instead of blocking forever.
    bool _liveEnded;

    // When a live demux is running it owns the pipe and routes scan/query replies
    // to the active serialized control exchange. With no live demux, the control
    // exchange owns the foreground reader. The ordering gate fences that handoff.
    // Reads frames and routes each JournalBatch to its drive's channel until the
    // broker dies, acknowledges the end of this demux's own watch generation, or the
    // demux is cancelled.
    async Task DemuxLoopAsync(uint watchGeneration, CancellationToken cancellationToken)
    {
        BrokerDiagnostics.Log($"DemuxLoopAsync started (t={Environment.CurrentManagedThreadId}).");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame == null)
                {
                    // EOF branch, before return: allow queries to report remaining-drive errors.
                    CompleteControlReplies(null);
                    // SignalBrokerDeath was already called inside ReadFrameAsync on EOF.
                    CompleteAllLiveChannels(new InvalidOperationException("Broker pipe closed: Pipe EOF"));
                    return;
                }

                var value = frame.Value;
                if (TryRouteControlFrame(value))
                {
                    continue;
                }
                if (value.Kind == BrokerFrameKind.EndWatchAck && value.WatchGeneration != watchGeneration)
                {
                    // The acknowledgement of an earlier generation whose stop stopped waiting
                    // for it. It ends nothing here: this watch belongs to a later generation.
                    BrokerDiagnostics.Log(FormattableString.Invariant(
                        $"Ignored an EndWatchAck for watch generation {value.WatchGeneration} in generation {watchGeneration}."));
                    continue;
                }
                if (value.Kind == BrokerFrameKind.EndWatchAck)
                {
                    // EndWatchAck branch, before return: an unexpected ack must not strand a scan.
                    CompleteControlReplies(new InvalidOperationException("Live watch ended during a broker control exchange."));
                    CompleteAllLiveChannels(null);
                    return; // clean stop: the watch was ended at the client's request
                }
                DispatchLiveFrame(value);
            }

            // The loop can also exit because cancellation was observed at the top of
            // an iteration, rather than by an already-blocked read throwing below -
            // complete the channels the same way the OperationCanceledException catch
            // does, so a subscriber's await-foreach ends instead of hanging forever.
            CompleteControlReplies(new InvalidOperationException("The broker demux stopped during a control exchange."));
            CompleteAllLiveChannels(null);
        }
        // Deliberate broad catch: any IO or protocol error on the pipe is broker death;
        // the watcher subscribers must see it as a fault. Cancellation ends quietly.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CompleteControlReplies(exception);
            SignalBrokerDeath(exception.Message);
            CompleteAllLiveChannels(new InvalidOperationException($"Broker pipe closed: {exception.Message}"));
        }
        catch (OperationCanceledException)
        {
            CompleteControlReplies(new InvalidOperationException("The broker demux stopped during a control exchange."));
            CompleteAllLiveChannels(null);
        }
    }

    void DispatchLiveFrame(BrokerFrame value)
    {
        switch (value.Kind)
        {
            case BrokerFrameKind.JournalBatch:
                {
                    var batchDrive = NormalizeDriveLetter(value.RequireDrive());
                    if (TryGetArmedLiveChannel(batchDrive, value.ArmEpoch) is { } batchChannel)
                    {
                        batchChannel.Writer.TryWrite(new LiveWatchItem.Batch(value.Entries, value.Cursor));
                    }
                    else
                    {
                        BrokerDiagnostics.Log($"Dropped a JournalBatch for drive {batchDrive} at arm epoch {value.ArmEpoch}.");
                    }
                    break;
                }

            case BrokerFrameKind.CaughtUp:
                {
                    var caughtUpDrive = NormalizeDriveLetter(value.RequireDrive());
                    if (TryGetArmedLiveChannel(caughtUpDrive, value.ArmEpoch) is { } caughtUpChannel)
                    {
                        caughtUpChannel.Writer.TryWrite(new LiveWatchItem.CaughtUpMarker());
                    }
                    else
                    {
                        BrokerDiagnostics.Log($"Dropped a CaughtUp frame for drive {caughtUpDrive} at arm epoch {value.ArmEpoch}.");
                    }
                    break;
                }

            case BrokerFrameKind.Error:
                {
                    var errorDrive = NormalizeDriveLetter(value.RequireDrive());
                    if (!TryFaultArmedLiveChannel(errorDrive, value.ArmEpoch, new InvalidOperationException(value.RequireMessage())))
                    {
                        BrokerDiagnostics.Log($"Dropped an Error frame for drive {errorDrive} at arm epoch {value.ArmEpoch}.");
                    }
                    break;
                }

            case BrokerFrameKind.Warning:
                FaultLiveChannel(NormalizeDriveLetter(value.RequireDrive()),
                    new InvalidOperationException(
                        $"Unexpected {BrokerFrameKind.Warning} frame on the live watch channel for drive " +
                        $"{value.RequireDrive()}: {value.RequireMessage()}"));
                break;
        }
    }

    Channel<LiveWatchItem> GetOrAddLiveChannel(string normalizedDrive)
    {
        lock (_liveChannelsLock)
        {
            return GetOrAddLiveChannelLocked(normalizedDrive);
        }
    }

    Channel<LiveWatchItem> GetOrAddLiveChannelLocked(string normalizedDrive)
    {
        if (!_liveChannels.TryGetValue(normalizedDrive, out var channel))
        {
            channel = Channel.CreateUnbounded<LiveWatchItem>();
            // If the broker already died, hand back an already-completed channel so
            // a late subscriber faults immediately rather than awaiting forever.
            if (_liveEnded)
            {
                channel.Writer.TryComplete(_liveEndError);
            }

            _liveChannels[normalizedDrive] = channel;
        }

        return channel;
    }

    // Replace the channel and pre-increment: the first arm is 1, never NoArmEpoch.
    uint ArmDriveLocked(string normalizedDrive)
    {
        if (_liveChannels.Remove(normalizedDrive, out var previous))
        {
            previous.Writer.TryComplete();
        }

        _lastArmEpoch++;
        _armedEpochsByDrive[normalizedDrive] = _lastArmEpoch;
        return _lastArmEpoch;
    }

    // Completing normally rather than with an error: a deliberate disarm is not a
    // failure, so this drive's subscriber ends its await-foreach instead of throwing.
    void DisarmDriveLocked(string normalizedDrive)
    {
        _armedEpochsByDrive.Remove(normalizedDrive);
        if (_liveChannels.Remove(normalizedDrive, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    Channel<LiveWatchItem>? TryGetArmedLiveChannel(
        string normalizedDrive, uint armEpoch)
    {
        lock (_liveChannelsLock)
        {
            return _armedEpochsByDrive.TryGetValue(normalizedDrive, out var armedEpoch) && armedEpoch == armEpoch
                ? GetOrAddLiveChannelLocked(normalizedDrive)
                : null;
        }
    }

    // Completes a single drive's channel with error, creating it first if no
    // subscriber has registered it yet - so a subscriber that calls CreateBatchSource
    // after this drive's Error frame arrived still gets an already-faulted channel
    // instead of awaiting a batch forever. Other drives are unaffected.
    void FaultLiveChannel(string normalizedDrive, Exception error)
    {
        lock (_liveChannelsLock)
        {
            FaultLiveChannelLocked(normalizedDrive, error);
        }
    }

    void FaultLiveChannelLocked(string normalizedDrive, Exception error)
    {
        // A failed drive receives nothing further until the client arms it again.
        _armedEpochsByDrive.Remove(normalizedDrive);
        if (!_liveChannels.TryGetValue(normalizedDrive, out var channel))
        {
            channel = Channel.CreateUnbounded<LiveWatchItem>();
            _liveChannels[normalizedDrive] = channel;
        }

        channel.Writer.TryComplete(error);
    }

    void CompleteAllLiveChannels(Exception? error)
    {
        lock (_liveChannelsLock)
        {
            _armedEpochsByDrive.Clear();
            _liveEnded = true;
            _liveEndError = error;
            _controlExchange?.Replies.Writer.TryComplete(error ??
                new InvalidOperationException("The broker demux ended during a control exchange."));
            foreach (var channel in _liveChannels.Values)
            {
                channel.Writer.TryComplete(error);
            }
        }
    }

    bool TryFaultArmedLiveChannel(string normalizedDrive, uint armEpoch, Exception error)
    {
        lock (_liveChannelsLock)
        {
            if (!_armedEpochsByDrive.TryGetValue(normalizedDrive, out var armedEpoch) || armedEpoch != armEpoch)
            {
                return false;
            }

            FaultLiveChannelLocked(normalizedDrive, error);
            return true;
        }
    }
}
