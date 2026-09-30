using System.Buffers;

namespace MFTLib;

/// <summary>
///     One drive pipe carrying one operation. It owns the pipe, its frame reader and its
///     diagnostics tag; disposing it closes the pipe, which the host reads as the operation's
///     cancellation.
/// </summary>
internal sealed class BrokerDriveChannel : IAsyncDisposable
{
    readonly BrokerPipeListener _pipe;
    readonly Stream _stream;
    readonly string _tag;
    readonly BrokerFrameReader _reader;
    readonly Action<BrokerDriveChannel> _closed;

    // Cancelled when the channel is disposed, so a read pending at that moment ends at once
    // whatever the stream does with a read it closes under. Never linked and never given a timer,
    // so it holds nothing to release.
    readonly CancellationTokenSource _closing = new();
    int _disposed;

    internal BrokerDriveChannel(char driveLetter, BrokerPipeListener pipe, Stream stream, string tag,
        Action<BrokerDriveChannel> closed, TimeProvider timeProvider)
    {
        DriveLetter = driveLetter;
        _pipe = pipe;
        _stream = stream;
        _tag = tag;
        _reader = new BrokerFrameReader(stream, driveLetter, tag, timeProvider);
        _closed = closed;
    }

    public char DriveLetter { get; }

    /// <summary>Writes one frame; a pipe that cannot take it is a lost channel.</summary>
    public async Task WriteAsync(Action<ArrayBufferWriter<byte>> write, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        BrokerDiagnostics.LogFrame(_tag, "write", buffer.WrittenSpan[4], buffer.WrittenCount - 4);
        try
        {
            await _stream.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new BrokerChannelLostException(DriveLetter,
                $"Drive {DriveLetter} broker channel could not be written: {exception.Message}", exception);
        }
    }

    /// <summary>
    ///     The next frame, or null once the host has closed its end. A read the channel's own
    ///     disposal cuts off (the process being disposed, say) is a lost channel, not a cancellation,
    ///     and so is a <see cref="BrokerFrameKind.Stalled" /> frame, which carries the host's message.
    /// </summary>
    public async ValueTask<BrokerFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
        try
        {
            var frame = await _reader.ReadAsync(read.Token).ConfigureAwait(false);
            return frame is { Kind: BrokerFrameKind.Stalled } stalled
                ? throw new BrokerChannelLostException(DriveLetter, stalled.RequireMessage())
                : frame;
        }
        catch (OperationCanceledException exception) when (_closing.IsCancellationRequested &&
                                                           !cancellationToken.IsCancellationRequested)
        {
            throw new BrokerChannelLostException(DriveLetter,
                $"Drive {DriveLetter} broker channel was closed while a read was pending.", exception);
        }
    }

    /// <summary>
    ///     Whether a lost read is this channel's own disposal cutting off a wait between frames: the
    ///     read was cancelled by the close, or began on the already closed pipe, before it consumed
    ///     any byte of a frame. A failure the peer causes (a truncated or malformed frame, a broken
    ///     pipe) is neither, even while the channel is closing, and a close that cuts a frame short
    ///     leaves that frame's bytes unaccounted for.
    /// </summary>
    public bool IsOwnClose(BrokerChannelLostException exception) =>
        _closing.IsCancellationRequested && !_reader.FrameStarted &&
        exception.InnerException is OperationCanceledException or ObjectDisposedException;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _closing.Cancel();
        await _reader.DisposeAsync().ConfigureAwait(false);
        _closed(this);
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
