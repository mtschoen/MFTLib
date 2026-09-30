using System.Buffers.Binary;

namespace MFTLib;

/// <summary>Reads whole frames off one broker pipe; the host and the client share it.</summary>
internal static class BrokerFrameStream
{
    // The largest legitimate frame is a scan's terminal JournalBatch, which carries every journal
    // entry from the cursor armed before the scan to the journal's tip; a watch batch is one 64 KB
    // native read. A wire entry (46 fixed bytes plus its UTF-16 name) is smaller than the native
    // USN_RECORD_V2 it was read from (60 fixed bytes plus the name, 8-byte aligned), so a batch is
    // smaller than the journal bytes it covers, and NTFS trims the journal back under its
    // MaximumSize plus one AllocationDelta. Windows creates 32 MB journals by default and servers
    // commonly run 512 MB; 1 GiB holds a whole journal of twice that server size, and it stays far
    // enough below Array.MaxLength that the 4-byte prefix plus the frame always fits one array.
    internal const int MaximumFrameLength = 1 << 30;

    /// <summary>
    ///     Returns the next frame, or null on a clean EOF before any byte of a frame. A pipe that
    ///     ends inside a frame throws <see cref="EndOfStreamException" />; a length prefix or payload
    ///     no frame can have throws <see cref="InvalidDataException" />. <paramref name="frameStarted" />
    ///     runs once the frame's first byte has been read, before the frame is complete.
    /// </summary>
    internal static async Task<BrokerFrame?> ReadFrameAsync(Stream stream, string channelTag,
        CancellationToken cancellationToken, Action? frameStarted = null)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cancellationToken, frameStarted).ConfigureAwait(false))
        {
            return null;
        }

        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (totalLength < 1)
        {
            throw new InvalidDataException($"Broker frame length {totalLength} is too short for a kind byte");
        }

        // Checked before the frame buffer is allocated, so a garbled prefix is malformed data
        // rather than an overflowed size or an exhausted heap.
        if (totalLength > MaximumFrameLength)
        {
            throw new InvalidDataException(
                $"Broker frame length {totalLength} exceeds the {MaximumFrameLength}-byte maximum");
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
    static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken,
        Action? firstByteRead = null)
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

            if (read == 0)
            {
                firstByteRead?.Invoke();
            }

            read += count;
        }

        return true;
    }
}
