using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace MFTLib;

/// <summary>
///     One host pipe (the control pipe or one drive pipe): serializes its frame writes under a
///     write lock, records whether it wrote since the heartbeat sender's last visit, and holds the
///     operation state its loop and its source publish. It is the <see cref="IBrokerOperationReporter" />
///     every source on that pipe receives. A frame write or a publication restarts the progress
///     clock; the heartbeat sender's own writes do not.
/// </summary>
[SuppressMessage("Design", "CA1001",
    Justification = "The write lock is never disposed: after the pipe's owner has ended, the heartbeat " +
                    "sender may still try it and a write in flight still releases it. SemaphoreSlim holds " +
                    "no kernel handle unless its AvailableWaitHandle is read, which nothing here does.")]
internal sealed class HostPipeWriter : IBrokerOperationReporter
{
    readonly Stream _stream;
    readonly TimeProvider _timeProvider;
    readonly bool _heartbeatsWhenIdle;
    readonly CancellationTokenSource _owner;
    readonly Action<string, ChannelOperationState>? _statePublished;

    // Held for the whole of one frame write. The heartbeat sender only ever tries it without
    // waiting, so a write blocked on this pipe never blocks the sender.
    readonly SemaphoreSlim _writeLock = new(1, 1);

    // Guards every field below; never held across a write or an await.
    readonly Lock _gate = new();
    ChannelOperationState _state;
    bool _wroteSinceVisit;
    bool _stalled;
    bool _closed;

    /// <summary>A pipe whose liveness writes and stall cancel the operation <paramref name="owner" /> runs.</summary>
    /// <param name="stream">The pipe.</param>
    /// <param name="tag">The pipe's diagnostics tag, which also names it in a stall message.</param>
    /// <param name="timeProvider">The host's clock.</param>
    /// <param name="heartbeatsWhenIdle">True for the control pipe, which heartbeats while it waits for a request.</param>
    /// <param name="owner">
    ///     The pipe's channel (or the session, for the control pipe). Its token bounds the heartbeat
    ///     sender's writes, and it is cancelled after a <see cref="BrokerFrameKind.Stalled" /> frame
    ///     or a failed heartbeat write, until <see cref="Close" /> runs; the owner disposes it only
    ///     after that.
    /// </param>
    /// <param name="statePublished">Test hook: receives the tag and the new state on every publication.</param>
    public HostPipeWriter(Stream stream, string tag, TimeProvider timeProvider, bool heartbeatsWhenIdle,
        CancellationTokenSource owner, Action<string, ChannelOperationState>? statePublished)
    {
        _stream = stream;
        Tag = tag;
        _timeProvider = timeProvider;
        _heartbeatsWhenIdle = heartbeatsWhenIdle;
        _owner = owner;
        _statePublished = statePublished;
        _state = new ChannelOperationState(ChannelOperationKind.Idle, string.Empty, timeProvider.GetUtcNow());
    }

    public string Tag { get; }

    public void WaitingOnVolume()
    {
        Publish(ChannelOperationKind.WaitingOnVolume, string.Empty);
    }

    public void Processing(string stepName)
    {
        ArgumentException.ThrowIfNullOrEmpty(stepName);
        Publish(ChannelOperationKind.Processing, stepName);
    }

    /// <summary>A scan waits for the parse-thread budget to admit it.</summary>
    public void Queued()
    {
        Publish(ChannelOperationKind.Queued, string.Empty);
    }

    void Publish(ChannelOperationKind kind, string step)
    {
        ChannelOperationState state;
        lock (_gate)
        {
            state = _state = new ChannelOperationState(kind, step, _timeProvider.GetUtcNow());
        }

        _statePublished?.Invoke(Tag, state);
    }

    /// <summary>
    ///     Writes one frame. A write that finds the pipe gone (IOException: "Pipe is broken" /
    ///     ERROR_NO_DATA) throws <see cref="ClientDisconnectedException" />: on a drive pipe that ends
    ///     only that channel, quietly, and on the control pipe it ends the session. A pipe that has
    ///     reported a stall writes nothing more and throws <see cref="OperationCanceledException" />.
    ///     Everything else a write can throw (cancellation, frame serialization) propagates unchanged.
    /// </summary>
    public async Task WriteFrameAsync(Action<ArrayBufferWriter<byte>> write, CancellationToken cancellationToken)
    {
        var buffer = Serialize(write);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_stalled)
                {
                    throw new OperationCanceledException($"Pipe {Tag} reported a stall and writes nothing more.");
                }

                _wroteSinceVisit = true;
                _state = _state with { Since = _timeProvider.GetUtcNow() };
            }

            await WriteBufferAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            throw new ClientDisconnectedException(exception);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Test hook: runs at the start of each visit, before the visit takes the write lock.</summary>
    internal Action? VisitStartingForTest { get; set; }

    /// <summary>
    ///     One heartbeat sender visit, on the sender's thread. Never blocks and never awaits a write.
    ///     A pipe whose write is in flight is skipped. Otherwise the visit holds the write lock, so no
    ///     frame can complete, and decides under the state lock, so no publication can land, from the
    ///     pipe's current state: a pipe that wrote since the last visit is skipped; the idle control
    ///     pipe, a WaitingOnVolume or Queued pipe, and a Processing pipe within
    ///     <see cref="BrokerLiveness.ProcessingLimit" /> of its last progress write Heartbeat; a
    ///     Processing pipe past the limit writes Stalled. The write this visit starts completes on its own.
    /// </summary>
    public void Visit(DateTimeOffset now)
    {
        VisitStartingForTest?.Invoke();
        if (!_writeLock.Wait(0))
        {
            // The write in flight is this interval's write.
            lock (_gate)
            {
                _wroteSinceVisit = false;
            }

            return;
        }

        Action<ArrayBufferWriter<byte>>? write;
        bool reportsStall;
        lock (_gate)
        {
            (write, reportsStall) = DecideLocked(now);
            _stalled |= reportsStall;
        }

        if (write is null)
        {
            _writeLock.Release();
            return;
        }

        _ = CompleteSenderWriteAsync(Serialize(write), reportsStall);
    }

    // Called with the write lock and the state lock held; clears the wrote-since-visit flag.
    (Action<ArrayBufferWriter<byte>>? Write, bool ReportsStall) DecideLocked(DateTimeOffset now)
    {
        if (_closed || _stalled)
        {
            return (null, false);
        }

        if (_wroteSinceVisit)
        {
            _wroteSinceVisit = false;
            return (null, false);
        }

        var state = _state;
        switch (state.Kind)
        {
            case ChannelOperationKind.Idle when _heartbeatsWhenIdle:
            case ChannelOperationKind.WaitingOnVolume:
            case ChannelOperationKind.Queued:
            case ChannelOperationKind.Processing when now - state.Since < BrokerLiveness.ProcessingLimit:
                return (BrokerProtocol.WriteHeartbeat, false);

            case ChannelOperationKind.Processing:
                var seconds = (now - state.Since).TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
                var message = $"Broker step '{state.Step}' on pipe {Tag} made no progress for {seconds} seconds.";
                return (writer => BrokerProtocol.WriteStalled(writer, message), true);

            default:
                return (null, false);
        }
    }

    /// <summary>The pipe's owner has ended: no sender write starts after this, and a failing one abandons nothing.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }

    // A failed sender write means the pipe is lost; a Stalled frame means the operation is.
    // Either way the pipe's owner is cancelled, once the write has let go of the pipe.
    async Task CompleteSenderWriteAsync(ArrayBufferWriter<byte> buffer, bool reportsStall)
    {
        var abandon = reportsStall;
        try
        {
            await WriteBufferAsync(buffer, _owner.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            BrokerDiagnostics.Log(Tag, $"Liveness write failed; abandoning the pipe: {exception.Message}");
            abandon = true;
        }
        finally
        {
            _writeLock.Release();
        }

        if (abandon)
        {
            await AbandonAsync().ConfigureAwait(false);
        }
    }

    Task AbandonAsync()
    {
        lock (_gate)
        {
            return _closed ? Task.CompletedTask : _owner.CancelAsync();
        }
    }

    ArrayBufferWriter<byte> Serialize(Action<ArrayBufferWriter<byte>> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        BrokerDiagnostics.LogFrame(Tag, "write", buffer.WrittenSpan[4], buffer.WrittenCount - 4);
        return buffer;
    }

    async Task WriteBufferAsync(ArrayBufferWriter<byte> buffer, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
