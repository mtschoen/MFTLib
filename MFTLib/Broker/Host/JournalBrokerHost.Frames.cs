using System.Buffers;
using System.Buffers.Binary;

namespace MFTLib;

/// <summary>Frame reads and writes on the control pipe and the drive pipes.</summary>
public sealed partial class JournalBrokerHost
{
    // One drive pipe and what its operation needs to write to it. The write lock keeps a scan's
    // progress pump and its operation from interleaving frames on this pipe only.
    sealed class DriveChannel(Stream stream, string drive, string tag, TimeProvider timeProvider) : IDisposable
    {
        public Stream Stream { get; } = stream;
        public string Drive { get; } = drive;
        public string Tag { get; } = tag;
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public BrokerOperationState Operation { get; } = new(timeProvider);

        public void Dispose()
        {
            WriteLock.Dispose();
        }
    }

    static Task WriteChannelFrameAsync(DriveChannel channel, Action<ArrayBufferWriter<byte>> write,
        CancellationToken cancellationToken)
    {
        return WriteFrameAsync(channel.Stream, channel.WriteLock, channel.Tag, write, cancellationToken);
    }

    static Task WriteChannelErrorAsync(DriveChannel channel, string message, CancellationToken cancellationToken)
    {
        return WriteChannelFrameAsync(channel, writer => BrokerProtocol.WriteError(writer, 0, message),
            cancellationToken);
    }

    // Writes one frame. A write that finds the pipe gone (IOException: "Pipe is broken" /
    // ERROR_NO_DATA) throws ClientDisconnectedException: on a drive pipe that ends only that
    // channel, quietly, and on the control pipe it ends the session. Everything else a write can
    // throw (cancellation, frame serialization) propagates unchanged.
    static async Task WriteFrameAsync(Stream stream, SemaphoreSlim writeLock, string channelTag,
        Action<ArrayBufferWriter<byte>> write, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        BrokerDiagnostics.LogFrame(channelTag, "write", buffer.WrittenSpan[4], buffer.WrittenCount - 4);

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            throw new ClientDisconnectedException(exception);
        }
        finally
        {
            writeLock.Release();
        }
    }

    // Returns null on a clean EOF before any byte of a frame.
    static async Task<BrokerFrame?> ReadFrameAsync(Stream stream, string channelTag, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (totalLength < 1)
        {
            throw new InvalidDataException($"Broker frame length {totalLength} is too short for a kind byte");
        }

        var frameBytes = new byte[4 + totalLength];
        header.CopyTo(frameBytes.AsMemory());
        if (!await ReadExactAsync(stream, frameBytes.AsMemory(4, totalLength), cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Truncated broker frame on pipe");
        }

        BrokerDiagnostics.LogFrame(channelTag, "read", frameBytes[4], totalLength);
        return BrokerProtocol.ReadFrame(frameBytes, out _);
    }

    // Fill buffer fully. Returns false on a clean EOF before any byte was read;
    // throws if the stream ends partway through (a corrupt/truncated frame).
    static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("Truncated broker frame on pipe");
            }

            read += count;
        }

        return true;
    }
}
