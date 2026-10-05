using System.Buffers;
using System.Globalization;

namespace MFTLib;

/// <summary>Control requests: request ids, the serialized control writes, and the one control reader.</summary>
public sealed partial class BrokerProcess
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
    ///     Read once, by the next allocation after it is set, as the id last issued, so a test can
    ///     move the counter next to <see cref="uint.MaxValue" /> without issuing billions of ids.
    /// </summary>
    internal Func<uint>? StartingRequestIdForTest { get; set; }

    /// <summary>The largest id the allocator issues; null is <see cref="uint.MaxValue" />.</summary>
    internal uint? MaximumRequestIdForTest { get; set; }

    internal int PendingRequestCountForTest
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    ///     The drive's NTFS sizing, as the broker reads it: the fields a block's capacity is
    ///     planned from (<see cref="NtfsVolumeInformation.MftValidDataLength" /> and
    ///     <see cref="NtfsVolumeInformation.BytesPerFileRecordSegment" />).
    /// </summary>
    /// <param name="driveLetter">The drive to query.</param>
    /// <param name="cancellationToken">
    ///     Stops waiting. Before the request starts writing nothing is sent; after, the reply is
    ///     dropped when it arrives.
    /// </param>
    /// <exception cref="InvalidOperationException">The broker could not query the drive.</exception>
    /// <exception cref="BrokerChannelLostException">The process ended first.</exception>
    /// <exception cref="TimeoutException">The broker did not answer within the reply timeout.</exception>
    internal async Task<NtfsVolumeInformation> QueryVolumeAsync(char driveLetter, CancellationToken cancellationToken)
    {
        var drive = NormalizeDrive(driveLetter);
        var reply = await RequestAsync((writer, requestId) => BrokerProtocol.WriteQueryVolume(writer, requestId, drive),
            BrokerFrameKind.VolumeInfo, cancellationToken).ConfigureAwait(false);
        return new NtfsVolumeInformation(reply.MftValidDataLength, reply.BytesPerFileRecordSegment);
    }

    /// <summary>
    ///     Grows the drive's USN journal (never shrinks it) and returns the sizing the volume
    ///     reports afterwards.
    /// </summary>
    /// <param name="driveLetter">The drive whose journal grows.</param>
    /// <param name="maximumSize">The new maximum size in bytes; the broker refuses one at or below the current.</param>
    /// <param name="allocationDelta">The new allocation delta in bytes.</param>
    /// <param name="cancellationToken">Stops waiting, as for <see cref="QueryVolumeAsync" />.</param>
    /// <exception cref="InvalidOperationException">The broker refused or failed the change.</exception>
    /// <exception cref="BrokerChannelLostException">The process ended first.</exception>
    /// <exception cref="TimeoutException">The broker did not answer within the reply timeout.</exception>
    public async Task<UsnJournalSettings> GrowUsnJournalAsync(char driveLetter, long maximumSize, long allocationDelta,
        CancellationToken cancellationToken)
    {
        var drive = NormalizeDrive(driveLetter);
        var reply = await RequestAsync((writer, requestId) =>
                BrokerProtocol.WriteGrowUsnJournal(writer, requestId, drive, maximumSize, allocationDelta),
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
            frame = await reply.Task.WaitAsync(ControlReplyTimeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"The broker did not answer request {requestId} within {Seconds(ControlReplyTimeout)} seconds.",
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
        // so the whole process ends, as a null-drive loss promises.
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

    // The next id after the last one issued that is neither zero nor still in the table.
    uint AllocateRequestIdLocked()
    {
        if (StartingRequestIdForTest is { } start)
        {
            _lastRequestId = start();
            StartingRequestIdForTest = null;
        }

        var maximum = MaximumRequestIdForTest ?? uint.MaxValue;
        if (_pending.Count >= maximum)
        {
            throw new InvalidOperationException("No broker request id is free");
        }

        var candidate = _lastRequestId;
        do
        {
            candidate = candidate >= maximum ? 1 : candidate + 1;
        } while (_pending.ContainsKey(candidate));

        _lastRequestId = candidate;
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
            AfterControlWriteLockAcquiredForTest?.Invoke();
            if (cancellationToken.IsCancellationRequested)
            {
                ReleaseRequest(requestId);
                cancellationToken.ThrowIfCancellationRequested();
            }

            BrokerDiagnostics.LogFrame(BrokerDiagnostics.ControlChannel, "write", buffer.WrittenSpan[4],
                buffer.WrittenCount - 4);
            using var timeout = new CancellationTokenSource(ControlReplyTimeout, _timeProvider);
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
                    ? $"A control request frame did not finish writing within {Seconds(ControlReplyTimeout)} seconds."
                    : $"A control request frame could not be written: {exception.Message}");
            }
        }
        finally
        {
            _controlWriteLock.Release();
        }
    }

    /// <summary>Runs once a control request holds the write lock, so a test can cancel its caller there.</summary>
    internal Action? AfterControlWriteLockAcquiredForTest { get; set; }

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
            failed = _pending.Values.ToArray();
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

    static string Seconds(TimeSpan duration)
    {
        return duration.TotalSeconds.ToString(CultureInfo.InvariantCulture);
    }
}
