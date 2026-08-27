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
///     Strings are length-prefixed UTF-16. This file holds the entry codec and the read
///     side; the control-frame write methods live in BrokerProtocol.Write.cs.
/// </summary>
public static partial class BrokerProtocol
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
            BrokerFrameKind.Warning => ReadWarningFrame(payload),
            BrokerFrameKind.QueryVolumes => BrokerFrame.QueryVolumes(ReadString(payload, 0, out _)),
            BrokerFrameKind.VolumeInfo => ReadVolumeInfoFrame(payload),
            _ => throw new InvalidDataException($"Unknown frame kind: {kind}")
        };
    }

    // Private read helpers

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

    static BrokerFrame ReadWarningFrame(ReadOnlySpan<byte> payload)
    {
        var drive = ReadString(payload, 0, out var offset);
        var message = ReadString(payload, offset, out _);
        return BrokerFrame.Warning(drive, message);
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

    static BrokerFrame ReadVolumeInfoFrame(ReadOnlySpan<byte> payload)
    {
        var drive = ReadString(payload, 0, out var offset);
        var mftRecordCount = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        offset += 8;
        var bytesPerFileRecordSegment = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
        offset += 4;
        var mftValidDataLength = BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]);
        return BrokerFrame.VolumeInfo(drive, mftRecordCount, bytesPerFileRecordSegment, mftValidDataLength);
    }
}
