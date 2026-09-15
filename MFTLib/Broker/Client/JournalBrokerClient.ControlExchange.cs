using System.Threading.Channels;

namespace MFTLib;

public sealed partial class JournalBrokerClient
{
    readonly CancellationTokenSource _controlCancellation = new();
    Exception? _controlFailure;
    int _disposeStarted;

    ControlExchange? _controlExchange; // Guarded by _liveChannelsLock.

    sealed class ControlExchange(bool throughDemux)
    {
        internal bool ThroughDemux { get; } = throughDemux;
        internal Channel<BrokerFrame> Replies { get; } = Channel.CreateUnbounded<BrokerFrame>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
        internal Func<BrokerFrame, bool> Accepts { get; set; } = _ => false;
        internal bool RequestInFlight { get; set; }
    }

    void ThrowIfControlUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (Volatile.Read(ref _controlFailure) is { } failure)
        {
            throw new InvalidOperationException("The broker control connection is no longer usable.", failure);
        }
        if (Volatile.Read(ref _brokerDeathSignaled) != 0)
        {
            throw new InvalidOperationException("The broker connection has ended.");
        }
    }

    void AbortControlExchange(Exception failure)
    {
        Interlocked.CompareExchange(ref _controlFailure, failure, null);
        CompleteControlReplies(failure);
        CompleteAllLiveChannels(new InvalidOperationException("The broker control exchange was interrupted.", failure));
        _controlCancellation.Cancel();
        SignalBrokerDeath(failure.Message);
    }

    async Task<T> RunControlExchangeAsync<T>(
        Func<ControlExchange, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        ThrowIfControlUnavailable();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _controlCancellation.Token);
        await _armOrderingGate.WaitAsync(linked.Token).ConfigureAwait(false);
        ControlExchange? exchange = null;
        try
        {
            ThrowIfControlUnavailable();
            lock (_liveChannelsLock)
            {
                ValidateClaimedGenerationLocked();
                exchange = new ControlExchange(_liveWatchGenerationStarted);
                _controlExchange = exchange;
            }
            return await operation(exchange, linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var cancelled = linked.IsCancellationRequested;
            if (exchange?.RequestInFlight == true && Volatile.Read(ref _disposeStarted) == 0)
            {
                AbortControlExchange(exception);
            }
            if (cancelled)
            {
                throw new OperationCanceledException(linked.Token);
            }
            throw;
        }
        finally
        {
            lock (_liveChannelsLock)
            {
                if (ReferenceEquals(_controlExchange, exchange))
                {
                    _controlExchange = null;
                }
            }
            exchange?.Replies.Writer.TryComplete();
            _armOrderingGate.Release();
        }
    }

    void ExpectControlReplies(ControlExchange exchange, Func<BrokerFrame, bool> accepts)
    {
        lock (_liveChannelsLock)
        {
            exchange.Accepts = accepts;
        }
    }

    bool TryRouteControlFrame(BrokerFrame frame)
    {
        lock (_liveChannelsLock)
        {
            if (_controlExchange is not { ThroughDemux: true } exchange || !exchange.Accepts(frame))
            {
                return false;
            }
            if (!exchange.Replies.Writer.TryWrite(frame))
            {
                throw new InvalidOperationException("The broker control reply channel has ended.");
            }
            return true;
        }
    }

    void CompleteControlReplies(Exception? error)
    {
        lock (_liveChannelsLock)
        {
            _controlExchange?.Replies.Writer.TryComplete(error);
        }
    }

    async Task<BrokerFrame?> ReadControlFrameAsync(ControlExchange exchange, CancellationToken token)
    {
        if (exchange.ThroughDemux)
        {
            while (await exchange.Replies.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                if (exchange.Replies.Reader.TryRead(out var frame))
                {
                    return frame;
                }
            }
            return null;
        }

        lock (_liveChannelsLock)
        {
            if (!ReferenceEquals(_controlExchange, exchange) || _liveWatchGenerationStarted || _demuxTask != null)
            {
                throw new InvalidOperationException("A foreground read cannot run beside the broker demux.");
            }
        }
        while (true)
        {
            var frame = await ReadFrameAsync(token).ConfigureAwait(false);
            if (frame == null || exchange.Accepts(frame.Value))
            {
                return frame;
            }
            // Old epoch-tagged live frames can remain after a stop timeout.
            if (frame.Value.Kind == BrokerFrameKind.Heartbeat ||
                frame.Value.Kind == BrokerFrameKind.EndWatchAck ||
                (frame.Value.Kind is BrokerFrameKind.JournalBatch or BrokerFrameKind.Error &&
                 frame.Value.ArmEpoch != BrokerFrame.NoArmEpoch))
            {
                continue;
            }
            throw new InvalidDataException($"Unexpected {frame.Value.Kind} in the broker control exchange.");
        }
    }
}
