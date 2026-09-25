using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MFTLib;

// Control-frame write methods for BrokerProtocol. See BrokerProtocol.cs for the entry
// codec, ReadFrame dispatch, and the private read helpers.
public static partial class BrokerProtocol
{
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

    // payload: [watchGeneration uint32][specLen int32][specBytes]
    public static void WriteStartWatch(IBufferWriter<byte> writer, uint watchGeneration, string drivesSpec)
    {
        var specBytes = Encoding.Unicode.GetBytes(drivesSpec);
        var totalLength = 1 + 4 + 4 + specBytes.Length;
        var span = writer.GetSpan(4 + totalLength);
        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], totalLength);
        offset += 4;
        span[offset] = (byte)BrokerFrameKind.StartWatch;
        offset += 1;
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], watchGeneration);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], specBytes.Length);
        offset += 4;
        specBytes.CopyTo(span[offset..]);
        offset += specBytes.Length;
        writer.Advance(offset);
    }

    public static void WriteDisarmDrive(IBufferWriter<byte> writer, string drive)
    {
        WriteFrameWithString(writer, BrokerFrameKind.DisarmDrive, drive);
    }

    public static void WriteShutdown(IBufferWriter<byte> writer)
    {
        WriteFrameNoPayload(writer, BrokerFrameKind.Shutdown);
    }

    public static void WriteHeartbeat(IBufferWriter<byte> writer)
    {
        WriteFrameNoPayload(writer, BrokerFrameKind.Heartbeat);
    }

    public static void WriteEndWatch(IBufferWriter<byte> writer, uint watchGeneration)
    {
        WriteWatchGenerationFrame(writer, BrokerFrameKind.EndWatch, watchGeneration);
    }

    public static void WriteEndWatchAck(IBufferWriter<byte> writer, uint watchGeneration)
    {
        WriteWatchGenerationFrame(writer, BrokerFrameKind.EndWatchAck, watchGeneration);
    }

    public static void WriteScanReady(IBufferWriter<byte> writer, string mmfName, long rowCount, long namePoolUsedBytes, long skippedRecordCount)
    {
        var nameBytes = Encoding.Unicode.GetBytes(mmfName);
        // payload: [nameLen int32][nameBytes][rowCount int64][namePoolUsedBytes int64][skippedRecordCount int64]
        var payloadLength = 4 + nameBytes.Length + 8 + 8 + 8;
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
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], rowCount);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], namePoolUsedBytes);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], skippedRecordCount);
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

    public static void WriteJournalBatch(IBufferWriter<byte> writer, string drive, uint armEpoch, UsnJournalCursor cursor,
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

        // payload: [driveLen int32][driveBytes][armEpoch uint32][journalId ulong][nextUsn long][entryCount int32][entryBytes]
        var payloadLength = 4 + driveBytes.Length + 4 + 8 + 8 + 4 + entryBuffer.WrittenCount;
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
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], armEpoch);
        offset += 4;
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

    public static void WriteError(IBufferWriter<byte> writer, string drive, uint armEpoch, string message)
    {
        var driveBytes = Encoding.Unicode.GetBytes(drive);
        var messageBytes = Encoding.Unicode.GetBytes(message);
        // payload: [driveLen int32][driveBytes][armEpoch uint32][messageLen int32][messageBytes]
        var payloadLength = 4 + driveBytes.Length + 4 + 4 + messageBytes.Length;
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
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], armEpoch);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], messageBytes.Length);
        offset += 4;
        messageBytes.CopyTo(span[offset..]);
        offset += messageBytes.Length;
        writer.Advance(offset);
    }

    public static void WriteCaughtUp(IBufferWriter<byte> writer, string drive, uint armEpoch)
    {
        var driveBytes = Encoding.Unicode.GetBytes(drive);
        // payload: [driveLen int32][driveBytes][armEpoch uint32]
        var payloadLength = 4 + driveBytes.Length + 4;
        var totalLength = 1 + payloadLength;
        var span = writer.GetSpan(4 + totalLength);
        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], totalLength);
        offset += 4;
        span[offset] = (byte)BrokerFrameKind.CaughtUp;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], driveBytes.Length);
        offset += 4;
        driveBytes.CopyTo(span[offset..]);
        offset += driveBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], armEpoch);
        offset += 4;
        writer.Advance(offset);
    }

    public static void WriteWarning(IBufferWriter<byte> writer, string drive, string message)
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
        span[offset] = (byte)BrokerFrameKind.Warning;
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
        ArgumentException.ThrowIfNullOrEmpty(progress.DriveLetter, nameof(progress.DriveLetter));
        var driveBytes = Encoding.Unicode.GetBytes(progress.DriveLetter);
        // payload: [driveLen int32][driveBytes][phase int32][recordsProcessed i64][bytesProcessed i64][totalRecordsOrMinusOne i64][totalBytesOrMinusOne i64][elapsedTicks i64]
        var payloadLength = 4 + driveBytes.Length + 4 + 8 + 8 + 8 + 8 + 8;
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
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], (int)progress.Phase);
        offset += 4;
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

    public static void WriteQueryVolumes(IBufferWriter<byte> writer, string drivesSpec)
    {
        WriteFrameWithString(writer, BrokerFrameKind.QueryVolumes, drivesSpec);
    }

    public static void WriteVolumeInfo(
        IBufferWriter<byte> writer, string drive, long mftRecordCount, uint bytesPerFileRecordSegment,
        long mftValidDataLength)
    {
        var driveBytes = Encoding.Unicode.GetBytes(drive);
        // payload: [driveLen int32][driveBytes][mftRecordCount i64][bytesPerFileRecordSegment u32][mftValidDataLength i64]
        var payloadLength = 4 + driveBytes.Length + 8 + 4 + 8;
        var totalLength = 1 + payloadLength;
        var span = writer.GetSpan(4 + totalLength);
        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], totalLength);
        offset += 4;
        span[offset] = (byte)BrokerFrameKind.VolumeInfo;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], driveBytes.Length);
        offset += 4;
        driveBytes.CopyTo(span[offset..]);
        offset += driveBytes.Length;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], mftRecordCount);
        offset += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], bytesPerFileRecordSegment);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], mftValidDataLength);
        offset += 8;
        writer.Advance(offset);
    }

    public static void WriteGrowUsnJournal(IBufferWriter<byte> writer, string drive,
        long maximumSize, long allocationDelta)
    {
        WriteDriveAndJournalSizes(writer, BrokerFrameKind.GrowUsnJournal, drive, maximumSize, allocationDelta);
    }

    public static void WriteUsnJournalSettings(IBufferWriter<byte> writer, string drive,
        long maximumSize, long allocationDelta)
    {
        WriteDriveAndJournalSizes(writer, BrokerFrameKind.UsnJournalSettings, drive, maximumSize, allocationDelta);
    }

    // Private write helpers

    // payload: [driveLen int32][driveBytes][maximumSize i64][allocationDelta i64]
    static void WriteDriveAndJournalSizes(IBufferWriter<byte> writer, BrokerFrameKind kind,
        string drive, long maximumSize, long allocationDelta)
    {
        var driveBytes = Encoding.Unicode.GetBytes(drive);
        var payloadLength = 4 + driveBytes.Length + 8 + 8;
        var totalLength = 1 + payloadLength;
        var span = writer.GetSpan(4 + totalLength);
        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], totalLength);
        offset += 4;
        span[offset] = (byte)kind;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], driveBytes.Length);
        offset += 4;
        driveBytes.CopyTo(span[offset..]);
        offset += driveBytes.Length;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], maximumSize);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], allocationDelta);
        offset += 8;
        writer.Advance(offset);
    }

    static void WriteFrameNoPayload(IBufferWriter<byte> writer, BrokerFrameKind kind)
    {
        // totalLength = 1 (just the kind byte, no payload)
        var span = writer.GetSpan(5);
        BinaryPrimitives.WriteInt32LittleEndian(span, 1);
        span[4] = (byte)kind;
        writer.Advance(5);
    }

    // payload: [watchGeneration uint32]
    static void WriteWatchGenerationFrame(IBufferWriter<byte> writer, BrokerFrameKind kind, uint watchGeneration)
    {
        var span = writer.GetSpan(9);
        BinaryPrimitives.WriteInt32LittleEndian(span, 5);
        span[4] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(span[5..], watchGeneration);
        writer.Advance(9);
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
}
