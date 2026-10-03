using System.Buffers.Binary;

namespace MFTLib;

/// <summary>Reads whole frames off one broker pipe; the host and the client share it.</summary>
internal static class BrokerFrameStream
{
    // The limit on a frame's kind byte plus payload, enforced by the writer before a prefix is
    // emitted and by the reader before a buffer is allocated. The largest frames the library
    // itself produces are a watch JournalBatch and an ArmAndScan request. A watch batch from the
    // native source is one 64 KiB read; a wire entry (46 fixed bytes plus its UTF-16 name) is
    // smaller than the native USN_RECORD_V2 it was read from (60 fixed bytes plus the name, 8-byte
    // aligned), so such a batch is under 64 KiB plus its 21-byte header. ArmAndScan carries the
    // caller's keep list: an NTFS name is at most 255 UTF-16 units (510 bytes) plus a 4-byte
    // length prefix, so 16 MiB holds 32,639 maximum-length names with the default section name,
    // and far more of the short ones a keep list names. Error and Stalled text is cut to 32,768
    // units. A keep list over the limit is refused by ScanDriveAsync before anything is sent, and
    // a custom journal source's batch over the limit ends that watch with an Error frame. 16 MiB
    // also bounds a garbled prefix to a modest allocation.
    internal const int MaximumFrameLength = 1 << 24;

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
