using System.Globalization;

namespace MFTLib;

/// <summary>
///     Reads the frames of one client pipe. Every failure other than cancellation surfaces as
///     <see cref="BrokerChannelLostException" /> naming the pipe's drive, or no drive for the
///     control pipe. A pipe that delivers no frame of any kind for <see cref="BrokerLiveness.StallLimit" />
///     while a read waits on it is closed and fails that read the same way.
/// </summary>
/// <remarks>
///     One timer serves the reader's whole life. It is armed for the limit, and each time it fires
///     without the limit having passed since the current read began it re-arms itself for the
///     remainder, so a frame costs a timestamp, not a timer. The clock starts when a read begins,
///     so time the consumer spends away from the pipe is not the host's silence.
///     Each read settles exactly once, as a frame or as a stall, under the reader's lock; a stall
///     that claims a read first wins even if the frame lands afterwards, and neither leaves state
///     for the next read.
/// </remarks>
internal sealed class BrokerFrameReader : IAsyncDisposable
{
    readonly Stream _stream;
    readonly char? _driveLetter;
    readonly string _channelTag;
    readonly TimeProvider _timeProvider;
    readonly Action _markFrameStarted;
    readonly Lock _gate = new();

    // Guarded by _gate.
    ITimer? _timer;
    PendingRead? _reading;
    long _readStarted;
    bool _disposed;

    public BrokerFrameReader(Stream stream, char? driveLetter, string channelTag, TimeProvider timeProvider)
    {
        _stream = stream;
        _driveLetter = driveLetter;
        _channelTag = channelTag;
        _timeProvider = timeProvider;
        _markFrameStarted = () => FrameStarted = true;
    }

    /// <summary>
    ///     Whether the latest read consumed any byte of a frame, including a frame it did not
    ///     finish because it failed or was cancelled.
    /// </summary>
    public bool FrameStarted { get; private set; }

    /// <summary>The next frame, or null once the host has closed its end.</summary>
    public async ValueTask<BrokerFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        FrameStarted = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var read = new PendingRead(cancellation);
        BeginRead(read);
        try
        {
            var frame = await BrokerFrameStream.ReadFrameAsync(_stream, _channelTag, cancellation.Token,
                _markFrameStarted).ConfigureAwait(false);
            return SettleAsFrame(read) ? frame : throw await CloseStalledAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
                                              or ObjectDisposedException or OperationCanceledException
                                          && HasStalled(read, cancellationToken))
        {
            throw await CloseStalledAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
                                              or ObjectDisposedException)
        {
            var pipe = _driveLetter is { } letter ? $"Drive {letter} broker channel" : "The broker control pipe";
            throw new BrokerChannelLostException(_driveLetter, $"{pipe} failed: {exception.Message}", exception);
        }
        finally
        {
            EndRead(read);
        }
    }

    /// <summary>
    ///     Stops the stall timer and waits for a callback already running. The stream stays the
    ///     owner's to close.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        ITimer? timer;
        lock (_gate)
        {
            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        if (timer is not null)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }
    }

    void BeginRead(PendingRead read)
    {
        lock (_gate)
        {
            _reading = read;
            _readStarted = _timeProvider.GetTimestamp();
            if (_timer is null && !_disposed)
            {
                _timer = _timeProvider.CreateTimer(_ => CheckStall(), null, BrokerLiveness.StallLimit,
                    Timeout.InfiniteTimeSpan);
            }
        }
    }

    // The frame wins unless the stall claimed this read first.
    bool SettleAsFrame(PendingRead read)
    {
        lock (_gate)
        {
            if (read.Stalled)
            {
                return false;
            }

            read.Settled = true;
            return true;
        }
    }

    void EndRead(PendingRead read)
    {
        lock (_gate)
        {
            read.Settled = true;
            if (ReferenceEquals(_reading, read))
            {
                _reading = null;
            }
        }
    }

    bool HasStalled(PendingRead read, CancellationToken callerToken)
    {
        lock (_gate)
        {
            return read.Stalled && !callerToken.IsCancellationRequested;
        }
    }

    // The pipe is closed so the host reads EOF, its cue that this operation is over.
    async Task<BrokerChannelLostException> CloseStalledAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        return new BrokerChannelLostException(_driveLetter,
            $"No frame from the broker for {BrokerLiveness.StallLimit.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds",
            new TimeoutException("The stall limit passed."));
    }

    // Runs when the timer is due: a read that has waited the whole limit and has not settled is
    // claimed as stalled and cancelled, and any other state re-arms the timer for what is left of
    // the limit.
    void CheckStall()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var waited = _timeProvider.GetElapsedTime(_readStarted);
            if (_reading is { Settled: false } read && waited >= BrokerLiveness.StallLimit)
            {
                read.Stalled = true;
                read.Cancellation.Cancel();
                return;
            }

            _timer?.Change(_reading is null || waited >= BrokerLiveness.StallLimit
                ? BrokerLiveness.StallLimit
                : BrokerLiveness.StallLimit - waited, Timeout.InfiniteTimeSpan);
        }
    }

    // One read's identity. Both flags are guarded by the reader's lock.
    sealed class PendingRead(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public bool Settled { get; set; }

        public bool Stalled { get; set; }
    }
}
