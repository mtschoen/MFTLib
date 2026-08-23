using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MFTLib;

/// <summary>
///     Binary frame codec for broker to UI IPC. Fixed-width little-endian numeric fields
///     followed by a length-prefixed UTF-16 filename. No pipes, no text parsing - replaces
///     the pipe-delimited helper serializers.
///     Every frame: [totalLength int32][kind byte][payload]
///     totalLength counts the kind byte plus payload bytes.
///     ReadFrame sets out consumed to the full frame length including the 4-byte length prefix.
///     Strings are length-prefixed UTF-16.
/// </summary>
public static class BrokerProtocol
{
    // Journal entry serialization

    public static void WriteEntry(IBufferWriter<byte> writer, UsnJournalEntry entry)
    {
        var nameBytes = Encoding.Unicode.GetBytes(entry.FileName);
        var span = writer.GetSpan(8 + 8 + 8 + 8 + 4 + 4 + 4 + nameBytes.Length);
        var offset = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], entry.RecordNumber);
        offset += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], entry.ParentRecordNumber);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], entry.Usn);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], entry.Timestamp.Ticks);
        offset += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], (uint)entry.Reason);
        offset += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], (uint)entry.FileAttributes);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], nameBytes.Length);
        offset += 4;
        nameBytes.CopyTo(span[offset..]);
        offset += nameBytes.Length;
        writer.Advance(offset);
    }

    public static UsnJournalEntry ReadEntry(ReadOnlySpan<byte> span, out int consumed)
    {
        var offset = 0;
        var recordNumber = BinaryPrimitives.ReadUInt64LittleEndian(span[offset..]);
        offset += 8;
        var parentRecordNumber = BinaryPrimitives.ReadUInt64LittleEndian(span[offset..]);
        offset += 8;
        var usn = BinaryPrimitives.ReadInt64LittleEndian(span[offset..]);
        offset += 8;
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(span[offset..]);
        offset += 8;
        var reason = BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
        offset += 4;
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
        offset += 4;
        var nameLength = BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);
        offset += 4;
        var fileName = Encoding.Unicode.GetString(span.Slice(offset, nameLength));
        offset += nameLength;
        consumed = offset;
        return UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = recordNumber,
            ParentRecordNumber = parentRecordNumber,
            Usn = usn,
            Timestamp = new DateTime(ticks, DateTimeKind.Utc),
            Reason = (UsnReason)reason,
            FileAttributes = (FileAttributes)attributes,
            FileName = fileName
        });
    }

    // Control frame write methods

    public static void WriteArmAndScan(IBufferWriter<byte> writer, string drivesSpec,
        IReadOnlyCollection<string>? keepFileNames = null)
    {
        var specBytes = Encoding.Unicode.GetBytes(drivesSpec);
        var nameBytes = (keepFileNames ?? Array.Empty<string>())
            .Select(Encoding.Unicode.GetBytes).ToArray();
        var namesLength = nameBytes.Sum(bytes => 4 + bytes.Length);

        // payload: [specLen int32][specBytes][nameCount int32][per name: nameLen int32][nameBytes]
        var payloadLength = 4 + specBytes.Length + 4 + namesLength;
        var totalLength = 1 + payloadLength;
        var span = writer.GetSpan(4 + totalLength);
        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], totalLength);
        offset += 4;
        span[offset] = (byte)BrokerFrameKind.ArmAndScan;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], specBytes.Length);
        offset += 4;
        specBytes.CopyTo(span[offset..]);
        offset += specBytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], nameBytes.Length);
        offset += 4;
        foreach (var bytes in nameBytes)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], bytes.Length);
            offset += 4;
            bytes.CopyTo(span[offset..]);
            offset += bytes.Length;
        }

        writer.Advance(offset);
    }

    public static void WriteStartWatch(IBufferWriter<byte> writer, string drivesSpec)
    {
        WriteFrameWithString(writer, BrokerFrameKind.StartWatch, drivesSpec);
    }

    public static void WriteShutdown(IBufferWriter<byte> writer)
    {
        WriteFrameNoPayload(writer, BrokerFrameKind.Shutdown);
    }

    public static void WriteHeartbeat(IBufferWriter<byte> writer)
    {
        WriteFrameNoPayload(writer, BrokerFrameKind.Heartbeat);
    }

    public static void WriteEndWatch(IBufferWriter<byte> writer)
    {
        WriteFrameNoPayload(writer, BrokerFrameKind.EndWatch);
    }

    public static void WriteEndWatchAck(IBufferWriter<byte> writer)
    {
        WriteFrameNoPayload(writer, BrokerFrameKind.EndWatchAck);
    }

    public static void WriteScanReady(IBufferWriter<byte> writer, string mmfName, long recordCount, long byteLength)
    {
        var nameBytes = Encoding.Unicode.GetBytes(mmfName);
        // payload: [nameLen int32][nameBytes][recordCount int64][byteLength int64]
        var payloadLength = 4 + nameBytes.Length + 8 + 8;
        var totalLength = 1 + payloadLength; // kind byte + payload
        var span = writer.GetSpan(4 + totalLength);
        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], totalLength);
        offset += 4;
        span[offset] = (byte)BrokerFrameKind.ScanReady;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], nameBytes.Length);
        offset += 4;
        nameBytes.CopyTo(span[offset..]);
        offset += nameBytes.Length;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], recordCount);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], byteLength);
        offset += 8;
        writer.Advance(offset);
    }

    public static void WriteCursor(IBufferWriter<byte> writer, string drive, UsnJournalCursor cursor)
    {
        var driveBytes = Encoding.Unicode.GetBytes(drive);
        // payload: [driveLen int32][driveBytes][journalId ulong][nextUsn long]
        var payloadLength = 4 + driveBytes.Length + 8 + 8;
        var totalLength = 1 + payloadLength;
        var span = writer.GetSpan(4 + totalLength);
        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], totalLength);
        offset += 4;
        span[offset] = (byte)BrokerFrameKind.Cursor;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], driveBytes.Length);
        offset += 4;
        driveBytes.CopyTo(span[offset..]);
        offset += driveBytes.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], cursor.JournalId);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], cursor.NextUsn);
        offset += 8;
        writer.Advance(offset);
    }

    public static void WriteJournalBatch(IBufferWriter<byte> writer, string drive, UsnJournalCursor cursor,
        UsnJournalEntry[] entries)
    {
        // We cannot compute the total size ahead of time without serializing entries first,
        // so serialize to a temp buffer then write the length prefix.
        var driveBytes = Encoding.Unicode.GetBytes(drive);
        var entryBuffer = new ArrayBufferWriter<byte>();
        foreach (var entry in entries)
        {
            WriteEntry(entryBuffer, entry);
        }

        // payload: [driveLen int32][driveBytes][journalId ulong][nextUsn long][entryCount int32][entryBytes]
        var payloadLength = 4 + driveBytes.Length + 8 + 8 + 4 + entryBuffer.WrittenCount;
        var totalLength = 1 + payloadLength;
        var span = writer.GetSpan(4 + totalLength);
        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], totalLength);
        offset += 4;
        span[offset] = (byte)BrokerFrameKind.JournalBatch;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], driveBytes.Length);
        offset += 4;
        driveBytes.CopyTo(span[offset..]);
        offset += driveBytes.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], cursor.JournalId);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], cursor.NextUsn);
        offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], entries.Length);
        offset += 4;
        entryBuffer.WrittenSpan.CopyTo(span[offset..]);
        offset += entryBuffer.WrittenCount;
        writer.Advance(offset);
    }

    public static void WriteError(IBufferWriter<byte> writer, string drive, string message)
    {
        var driveBytes = Encoding.Unicode.GetBytes(drive);
        var messageBytes = Encoding.Unicode.GetBytes(message);
        // payload: [driveLen int32][driveBytes][messageLen int32][messageBytes]
        var payloadLength = 4 + driveBytes.Length + 4 + messageBytes.Length;
        var totalLength = 1 + payloadLength;
        var span = writer.GetSpan(4 + totalLength);
        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], totalLength);
        offset += 4;
        span[offset] = (byte)BrokerFrameKind.Error;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], driveBytes.Length);
        offset += 4;
        driveBytes.CopyTo(span[offset..]);
        offset += driveBytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], messageBytes.Length);
        offset += 4;
        messageBytes.CopyTo(span[offset..]);
        offset += messageBytes.Length;
        writer.Advance(offset);
    }

    public static void WriteScanProgress(IBufferWriter<byte> writer, BrokerScanProgress progress)
    {
        var driveBytes = Encoding.Unicode.GetBytes(progress.DriveLetter);
        // payload: [driveLen int32][driveBytes][recordsProcessed i64][bytesProcessed i64][totalRecordsOrMinusOne i64][totalBytesOrMinusOne i64][elapsedTicks i64]
        var payloadLength = 4 + driveBytes.Length + 8 + 8 + 8 + 8 + 8;
        var totalLength = 1 + payloadLength;
        var span = writer.GetSpan(4 + totalLength);
        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], totalLength);
        offset += 4;
        span[offset] = (byte)BrokerFrameKind.ScanProgress;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], driveBytes.Length);
        offset += 4;
        driveBytes.CopyTo(span[offset..]);
        offset += driveBytes.Length;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], progress.RecordsProcessed);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], progress.BytesProcessed);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], progress.TotalRecords ?? -1L);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], progress.TotalBytes ?? -1L);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], progress.Elapsed.Ticks);
        offset += 8;
        writer.Advance(offset);
    }

    public static BrokerFrame ReadFrame(ReadOnlySpan<byte> span, out int consumed)
    {
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(span);
        consumed = 4 + totalLength;
        var payload = span.Slice(5, totalLength - 1); // skip 4-byte prefix + 1 kind byte
        var kind = (BrokerFrameKind)span[4];

        return kind switch
        {
            BrokerFrameKind.ArmAndScan => ReadArmAndScanFrame(payload),
            BrokerFrameKind.StartWatch => BrokerFrame.StartWatch(ReadString(payload, 0, out _)),
            BrokerFrameKind.Shutdown => BrokerFrame.Shutdown(),
            BrokerFrameKind.Heartbeat => BrokerFrame.Heartbeat(),
            BrokerFrameKind.EndWatch => BrokerFrame.EndWatch(),
            BrokerFrameKind.EndWatchAck => BrokerFrame.EndWatchAck(),
            BrokerFrameKind.ScanReady => ReadScanReadyFrame(payload),
            BrokerFrameKind.Cursor => ReadCursorFrame(payload),
            BrokerFrameKind.JournalBatch => ReadJournalBatchFrame(payload),
            BrokerFrameKind.Error => ReadErrorFrame(payload),
            BrokerFrameKind.ScanProgress => ReadScanProgressFrame(payload),
            _ => throw new InvalidDataException($"Unknown frame kind: {kind}")
        };
    }

    // Private helpers

    static void WriteFrameNoPayload(IBufferWriter<byte> writer, BrokerFrameKind kind)
    {
        // totalLength = 1 (just the kind byte, no payload)
        var span = writer.GetSpan(5);
        BinaryPrimitives.WriteInt32LittleEndian(span, 1);
        span[4] = (byte)kind;
        writer.Advance(5);
    }

    static void WriteFrameWithString(IBufferWriter<byte> writer, BrokerFrameKind kind, string value)
    {
        var valueBytes = Encoding.Unicode.GetBytes(value);
        // payload: [valueLen int32][valueBytes]
        var payloadLength = 4 + valueBytes.Length;
        var totalLength = 1 + payloadLength;
        var span = writer.GetSpan(4 + totalLength);
        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], totalLength);
        offset += 4;
        span[offset] = (byte)kind;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], valueBytes.Length);
        offset += 4;
        valueBytes.CopyTo(span[offset..]);
        offset += valueBytes.Length;
        writer.Advance(offset);
    }

    static string ReadString(ReadOnlySpan<byte> span, int offset, out int end)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);
        offset += 4;
        var value = Encoding.Unicode.GetString(span.Slice(offset, length));
        offset += length;
        end = offset;
        return value;
    }

    static BrokerFrame ReadArmAndScanFrame(ReadOnlySpan<byte> payload)
    {
        var drivesSpec = ReadString(payload, 0, out var offset);
        var nameCount = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        offset += 4;
        var keepFileNames = new string[nameCount];
        for (var i = 0; i < nameCount; i++)
        {
            keepFileNames[i] = ReadString(payload, offset, out offset);
        }

        return BrokerFrame.ArmAndScan(drivesSpec, keepFileNames);
    }

    static BrokerFrame ReadScanReadyFrame(ReadOnlySpan<byte> payload)
    {
        var mmfName = ReadString(payload, 0, out var offset);
        var recordCount = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += 8;
        var byteLength = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        return BrokerFrame.ScanReady(mmfName, recordCount, byteLength);
    }

    static BrokerFrame ReadCursorFrame(ReadOnlySpan<byte> payload)
    {
        var drive = ReadString(payload, 0, out var offset);
        var journalId = BinaryPrimitives.ReadUInt64LittleEndian(payload[offset..]);
        offset += 8;
        var nextUsn = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        return BrokerFrame.ArmedCursor(drive, new UsnJournalCursor(journalId, nextUsn));
    }

    static BrokerFrame ReadJournalBatchFrame(ReadOnlySpan<byte> payload)
    {
        var drive = ReadString(payload, 0, out var offset);
        var journalId = BinaryPrimitives.ReadUInt64LittleEndian(payload[offset..]);
        offset += 8;
        var nextUsn = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += 8;
        var entryCount = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        offset += 4;
        var entries = new UsnJournalEntry[entryCount];
        for (var i = 0; i < entryCount; i++)
        {
            entries[i] = ReadEntry(payload[offset..], out var entryConsumed);
            offset += entryConsumed;
        }

        return BrokerFrame.JournalBatch(drive, new UsnJournalCursor(journalId, nextUsn), entries);
    }

    static BrokerFrame ReadErrorFrame(ReadOnlySpan<byte> payload)
    {
        var drive = ReadString(payload, 0, out var offset);
        var message = ReadString(payload, offset, out _);
        return BrokerFrame.Error(drive, message);
    }

    static BrokerFrame ReadScanProgressFrame(ReadOnlySpan<byte> payload)
    {
        var drive = ReadString(payload, 0, out var offset);
        var recordsProcessed = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += 8;
        var bytesProcessed = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += 8;
        var totalRecordsRaw = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += 8;
        var totalBytesRaw = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += 8;
        var elapsedTicks = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);

        long? totalRecords = totalRecordsRaw >= 0 ? totalRecordsRaw : null;
        long? totalBytes = totalBytesRaw >= 0 ? totalBytesRaw : null;
        var elapsed = TimeSpan.FromTicks(elapsedTicks);

        var progress =
            new BrokerScanProgress(drive, recordsProcessed, bytesProcessed, totalRecords, totalBytes, elapsed);
        return BrokerFrame.ScanProgress(progress);
    }
}
