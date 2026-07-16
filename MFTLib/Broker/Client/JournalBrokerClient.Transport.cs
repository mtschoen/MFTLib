using System.Buffers;
using System.Buffers.Binary;

namespace MFTLib;

public sealed partial class JournalBrokerClient
{
    /// <summary>
    /// Send a <c>Shutdown</c> frame (best-effort), wind down the live-watch demux,
    /// dispose all MMF lifetimes, and close the pipe.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Best-effort Shutdown: if the pipe is already gone, swallow the error.
        // Deliberate broad catch: the pipe may be in any state at dispose time -
        // broken, closed, or mid-frame. We must not throw from DisposeAsync.
        try
        {
            await WriteFrameAsync(BrokerProtocol.WriteShutdown, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Swallowed intentionally: pipe may already be closed. BrokerDied was
            // already fired if EOF was seen during normal operation.
        }

        // Cancel the demux so its blocking pipe read unwinds, then await it before
        // disposing the pipe (cancelling the read is what reliably unblocks it).
        if (_demuxCts != null)
            await _demuxCts.CancelAsync().ConfigureAwait(false);

        // DemuxLoopAsync catches everything internally (broker death and cancellation
        // alike) and never lets an exception escape, so awaiting it here cannot fault.
        if (_demuxTask != null)
            await _demuxTask.ConfigureAwait(false);

        _demuxCts?.Dispose();
        await _pipe.DisposeAsync().ConfigureAwait(false);

        List<IDisposable> lifetimes;
        lock (_mmfLifetimesLock)
        {
            lifetimes = new List<IDisposable>(_mmfLifetimes);
            _mmfLifetimes.Clear();
        }

        foreach (var lifetime in lifetimes)
            lifetime.Dispose();

        _writeLock.Dispose();
    }

    // Fires BrokerDied at most once per client lifetime (guarded by Interlocked).
    void SignalBrokerDeath(string reason)
    {
        if (Interlocked.Exchange(ref _brokerDeathSignaled, 1) == 0)
            BrokerDied?.Invoke(reason);
    }

    // Shared frame-reading helper. Returns null on clean EOF.
    async Task<BrokerFrame?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(_pipe, header, cancellationToken).ConfigureAwait(false))
        {
            SignalBrokerDeath("Pipe EOF");
            return null;
        }

        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        if (!await ReadExactAsync(_pipe, frameBytes.AsMemory(4, totalLength), cancellationToken).ConfigureAwait(false))
            throw new EndOfStreamException("Truncated broker frame on pipe");

        if (totalLength >= 1)
            BrokerDiagnostics.LogFrame("read", frameBytes[4], totalLength);
        return BrokerProtocol.ReadFrame(frameBytes, out _);
    }

    Task WriteFrameAsync(Action<ArrayBufferWriter<byte>> write, CancellationToken cancellationToken) =>
        WriteFrameAsync(write, transmissionStarted: null, cancellationToken);

    async Task WriteFrameAsync(
        Action<ArrayBufferWriter<byte>> write,
        Action? transmissionStarted,
        CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        if (buffer.WrittenCount >= 5)
            BrokerDiagnostics.LogFrame("write", buffer.WrittenSpan[4], buffer.WrittenCount - 4);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            transmissionStarted?.Invoke();
            await _pipe.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // Normalize a drive path ("C:\\", "C:", "C") to the bare single letter ("C").
    // The broker spec tokens and frame Drive fields use the bare letter.
    internal static string NormalizeDriveLetter(string drive) =>
        drive.TrimEnd(':', '\\', '/');

    // Fill buffer fully. Returns false on clean EOF before any byte; throws on truncated data.
    static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (read == 0)
                    return false;
                throw new EndOfStreamException("Truncated broker frame on pipe");
            }
            read += count;
        }
        return true;
    }
}
