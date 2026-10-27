using System.Buffers;
using MFTLib.Index;

namespace MFTLib;

/// <summary>Control requests: request ids, the serialized control writes, and the one control reader.</summary>
sealed partial class BrokerProcess
{
    // Every request whose reply has not arrived, keyed by request id, including requests whose
    // caller stopped waiting: an entry leaves only when its reply arrives or the process ends, so
    // a late reply can never be taken for a newer request's.
    readonly Dictionary<uint, TaskCompletionSource<BrokerFrame>> _pending = [];
    readonly SemaphoreSlim _controlWriteLock = new(1, 1);
    uint _lastRequestId;

    // Why the process ended; null while it runs.
    string? _endReason;


    /// <summary>
    ///     The volume's NTFS sizing, as the broker reads it: the fields a block's capacity is
    ///     planned from (<see cref="NtfsVolumeInformation.MftValidDataLength" /> and
    ///     <see cref="NtfsVolumeInformation.BytesPerFileRecordSegment" />).
    /// </summary>
    /// <param name="volume">The volume to query.</param>
    /// <param name="cancellationToken">
    ///     Stops waiting. Before the request starts writing nothing is sent; after, the reply is
    ///     dropped when it arrives.
    /// </param>
    /// <exception cref="InvalidOperationException">The broker could not query the volume.</exception>
    /// <exception cref="BrokerChannelLostException">The process ended first.</exception>
    /// <exception cref="TimeoutException">The broker did not answer within the reply timeout.</exception>
    internal async Task<NtfsVolumeInformation> QueryVolumeAsync(VolumeKey volume, CancellationToken cancellationToken)
    {
        volume = volume.Validated();
        var reply = await RequestAsync((writer, requestId) => BrokerProtocol.WriteQueryVolume(writer, BrokerFrame.QueryVolume(requestId, volume)),
            BrokerFrameKind.VolumeInfo, cancellationToken).ConfigureAwait(false);
        return new NtfsVolumeInformation(reply.MftValidDataLength, reply.BytesPerFileRecordSegment);
    }

    /// <summary>
    ///     Grows the volume's USN journal (never shrinks it) and returns the sizing the volume
    ///     reports afterwards.
    /// </summary>
    /// <param name="volume">The volume whose journal grows.</param>
    /// <param name="target">The requested maximum size and allocation delta in bytes; the broker refuses a maximum at or below the current.</param>
    /// <param name="cancellationToken">Stops waiting, as for <see cref="QueryVolumeAsync" />.</param>
    /// <exception cref="InvalidOperationException">The broker refused or failed the change.</exception>
    /// <exception cref="BrokerChannelLostException">The process ended first.</exception>
    /// <exception cref="TimeoutException">The broker did not answer within the reply timeout.</exception>
    internal async Task<UsnJournalSettings> GrowUsnJournalAsync(VolumeKey volume, UsnJournalSettings target,
        CancellationToken cancellationToken)
    {
        volume = volume.Validated();
        var reply = await RequestAsync((writer, requestId) =>
                BrokerProtocol.WriteGrowUsnJournal(writer, BrokerFrame.GrowUsnJournal(requestId, volume, target.MaximumSize, target.AllocationDelta)),
            BrokerFrameKind.UsnJournalSettings, cancellationToken).ConfigureAwait(false);
        return new UsnJournalSettings
        {
            MaximumSize = reply.JournalMaximumSize,
            AllocationDelta = reply.JournalAllocationDelta
        };
    }

    // Sends one control request and returns its reply. An Error reply throws
    // InvalidOperationException with the host's message; a reply that takes longer than
    // ControlReplyTimeout throws TimeoutException and leaves the request's entry to be dropped
    // when the late reply arrives.
    async Task<BrokerFrame> RequestAsync(Action<ArrayBufferWriter<byte>, uint> write, BrokerFrameKind expectedReply,
        CancellationToken cancellationToken)
    {
        var (requestId, reply) = RegisterRequest();
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer, requestId);
        await WriteControlRequestAsync(requestId, buffer, cancellationToken).ConfigureAwait(false);

        // A write that failed has already ended the process, which fails this reply too. A caller
        // that cancelled while its frame was being finished has stopped waiting, even if the reply
        // is already here: its entry stays until the reply is dropped.
        cancellationToken.ThrowIfCancellationRequested();
        BrokerFrame frame;
        try
        {
            frame = await reply.Task.WaitAsync(BrokerLiveness.ControlReplyTimeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"The broker did not answer request {requestId} within {BrokerLiveness.FormatSeconds(BrokerLiveness.ControlReplyTimeout)} seconds.",
                exception);
        }

        if (frame.Kind == BrokerFrameKind.Error)
        {
            throw new InvalidOperationException(frame.RequireMessage());
        }

        if (frame.Kind == expectedReply)
        {
            return frame;
        }

        // A known reply of the wrong kind means the control session is out of step with the host,
        // so the whole process ends, as a null-volume loss promises.
        var reason = $"The broker answered request {requestId} with {frame.Kind} instead of {expectedReply}.";
        RequestEnd(reason);
        throw new BrokerChannelLostException(null, reason);
    }

    (uint RequestId, TaskCompletionSource<BrokerFrame> Reply) RegisterRequest()
    {
        lock (_gate)
        {
            if (_endReason is { } reason)
            {
                throw new BrokerChannelLostException(null, reason);
            }

            var requestId = AllocateRequestIdLocked();
            var reply = new TaskCompletionSource<BrokerFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(requestId, reply);
            return (requestId, reply);
        }
    }

    uint AllocateRequestIdLocked()
    {
        if ((uint)_pending.Count >= uint.MaxValue)
        {
            throw new InvalidOperationException("No broker request id is free");
        }

        _lastRequestId = NextRequestId(_lastRequestId, _pending);
        return _lastRequestId;
    }

    // The next id after the last one issued that is neither zero nor still in the table. The
    // caller guarantees the table does not hold every id, so the walk ends.
    internal static uint NextRequestId<TReply>(uint lastIssued, Dictionary<uint, TReply> outstanding)
    {
        var candidate = lastIssued;
        do
        {
            candidate = candidate == uint.MaxValue ? 1 : candidate + 1;
        } while (outstanding.ContainsKey(candidate));

        return candidate;
    }

    // The caller's token is observed only until the frame starts writing; cancellation before
    // that sends nothing and releases the id. A started frame is finished under the process's own
    // token, bounded by ControlReplyTimeout, because the host's reader requires every advertised
    // byte and a partial frame could never be followed by another. A write that fails or times
    // out ends the process.
    async Task WriteControlRequestAsync(uint requestId, ArrayBufferWriter<byte> buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            await _controlWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReleaseRequest(requestId);
            throw;
        }

        try
        {
            Hooks.AfterControlWriteLockAcquired?.Invoke();
            if (cancellationToken.IsCancellationRequested)
            {
                ReleaseRequest(requestId);
                cancellationToken.ThrowIfCancellationRequested();
            }

            BrokerDiagnostics.LogFrame(BrokerDiagnostics.ControlChannel, "write", buffer.WrittenSpan[4],
                buffer.WrittenCount - 4);
            using var timeout = new CancellationTokenSource(BrokerLiveness.ControlReplyTimeout, _timeProvider);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, timeout.Token);
            try
            {
                await _control.WriteAsync(buffer.WrittenMemory, bound.Token).ConfigureAwait(false);
                await _control.FlushAsync(bound.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                                  or OperationCanceledException)
            {
                RequestEnd(timeout.IsCancellationRequested
                    ? $"A control request frame did not finish writing within {BrokerLiveness.FormatSeconds(BrokerLiveness.ControlReplyTimeout)} seconds."
                    : $"A control request frame could not be written: {exception.Message}");
            }
        }
        finally
        {
            _controlWriteLock.Release();
        }
    }

    void ReleaseRequest(uint requestId)
    {
        lock (_gate)
        {
            _pending.Remove(requestId);
        }
    }

    async Task ReadControlAsync()
    {
        var reader = new BrokerFrameReader(_control, null, BrokerDiagnostics.ControlChannel, _timeProvider);
        string reason;
        try
        {
            reason = await RouteControlFramesAsync(reader).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            reason = "The broker process ended.";
        }
        catch (BrokerChannelLostException exception)
        {
            reason = exception.Message;
        }
        // Nothing but disposal awaits this reader, so any other failure of the pipe (a stream
        // that refuses reads once closed, say) ends the process with its message instead of
        // faulting a task nobody observes.
        catch (Exception exception)
        {
            reason = $"The broker control pipe failed: {exception.Message}";
        }

        // The stall timer drains before the process is reported ended, so no callback outlives it.
        // A reader that fails to drain must still end the process, or Ended would never complete.
        try
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            End(reason);
        }
    }

    // Routes replies to their requests until the pipe ends or carries a frame the control pipe
    // cannot; returns why it stopped.
    async Task<string> RouteControlFramesAsync(BrokerFrameReader reader)
    {
        while (await reader.ReadAsync(_lifetime.Token).ConfigureAwait(false) is { } frame)
        {
            switch (frame.Kind)
            {
                case BrokerFrameKind.ChannelOpened or BrokerFrameKind.VolumeInfo or BrokerFrameKind.UsnJournalSettings
                    or BrokerFrameKind.Error:
                    CompleteRequest(frame);
                    break;

                case BrokerFrameKind.Heartbeat:
                    break;

                case BrokerFrameKind.Stalled:
                    return frame.RequireMessage();

                default:
                    return $"The broker sent {frame.Kind} on the control pipe.";
            }
        }

        return "The broker closed its control pipe.";
    }

    // A reply with no pending entry answers nothing still tracked, so it is dropped.
    void CompleteRequest(BrokerFrame frame)
    {
        TaskCompletionSource<BrokerFrame>? reply;
        lock (_gate)
        {
            _pending.Remove(frame.RequestId, out reply);
        }

        reply?.TrySetResult(frame);
    }

    // Ends the process once: closes the control pipe, fails and releases every pending request,
    // then completes Ended. A reason recorded by RequestEnd wins over what the reader saw, because
    // the reader's failure is only the consequence of that request.
    void End(string observed)
    {
        string reason;
        TaskCompletionSource<BrokerFrame>[] failed;
        lock (_gate)
        {
            reason = _endRequestedReason ?? observed;
            _endReason = reason;
            failed = [.. _pending.Values];
            _pending.Clear();
        }

        _lifetime.Cancel();
        try
        {
            _control.Dispose();
        }
        catch (Exception exception)
        {
            BrokerDiagnostics.Log(BrokerDiagnostics.ControlChannel,
                $"Closing the control pipe failed: {exception}");
        }

        foreach (var reply in failed)
        {
            reply.TrySetException(new BrokerChannelLostException(null, reason));
        }

        _ended.TrySetResult(reason);
    }
}
