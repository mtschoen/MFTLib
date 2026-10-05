using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using MFTLib.Index;

namespace MFTLib;

/// <summary>
///     Binary frame codec for broker IPC. Fixed-width little-endian numeric fields and
///     length-prefixed UTF-16 strings.
///     Every frame: [totalLength int32][kind byte][payload]
///     totalLength counts the kind byte plus payload bytes.
///     ReadFrame sets out consumed to the full frame length including the 4-byte length prefix.
///     This file holds the entry codec and the read side; the write methods live in
///     BrokerProtocol.Write.cs and the payload primitives in BrokerProtocol.Payload.cs.
/// </summary>
internal static partial class BrokerProtocol
{
    // Journal entry serialization

    public static void WriteEntry(IBufferWriter<byte> writer, UsnJournalEntry entry)
    {
        var nameBytes = Encoding.Unicode.GetBytes(entry.FileName);
        var span = writer.GetSpan(8 + 8 + 2 + 8 + 8 + 4 + 4 + 4 + nameBytes.Length);
        var offset = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], entry.RecordNumber);
        offset += 8;
        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], entry.ParentRecordNumber);
        offset += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], entry.SequenceNumber);
        offset += 2;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], entry.Usn);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span[offset..], entry.TimestampUtc.Ticks);
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
        var sequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(span[offset..]);
        offset += 2;
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
            SequenceNumber = sequenceNumber,
            Usn = usn,
            TimestampUtc = new DateTime(ticks, DateTimeKind.Utc),
            Reason = (UsnReason)reason,
            FileAttributes = (FileAttributes)attributes,
            FileName = fileName
        });
    }

    public static BrokerFrame ReadFrame(ReadOnlySpan<byte> span, out int consumed)
    {
        var totalLength = BinaryPrimitives.ReadInt32LittleEndian(span);
        consumed = 4 + totalLength;
        var kind = (BrokerFrameKind)span[4];
        var payload = new PayloadReader(span.Slice(5, totalLength - 1)); // skip 4-byte prefix + 1 kind byte

        return kind switch
        {
            BrokerFrameKind.OpenChannel => BrokerFrame.OpenChannel(
                payload.UInt32(), payload.String(), payload.String()),
            BrokerFrameKind.ChannelOpened => BrokerFrame.ChannelOpened(payload.UInt32()),
            BrokerFrameKind.QueryVolume => BrokerFrame.QueryVolume(payload.UInt32(), payload.String()),
            BrokerFrameKind.VolumeInfo => BrokerFrame.VolumeInfo(
                payload.UInt32(), payload.UInt32(), payload.Int64()),
            BrokerFrameKind.GrowUsnJournal => BrokerFrame.GrowUsnJournal(
                payload.UInt32(), payload.String(), payload.Int64(), payload.Int64()),
            BrokerFrameKind.UsnJournalSettings => BrokerFrame.UsnJournalSettings(
                payload.UInt32(), payload.Int64(), payload.Int64()),
            BrokerFrameKind.Error => BrokerFrame.Error(payload.UInt32(), payload.String()),
            BrokerFrameKind.Heartbeat => BrokerFrame.Heartbeat(),
            BrokerFrameKind.Stalled => BrokerFrame.Stalled(payload.String()),
            BrokerFrameKind.ArmAndScan => ReadArmAndScanFrame(ref payload),
            BrokerFrameKind.Cursor => BrokerFrame.ArmedCursor(ReadCursor(ref payload)),
            BrokerFrameKind.ScanProgress => BrokerFrame.ScanProgress(ReadScanProgress(ref payload)),
            BrokerFrameKind.CatchUpLost => ReadCatchUpLostFrame(ref payload),
            BrokerFrameKind.ScanReady => BrokerFrame.ScanReady(payload.Int64()),
            BrokerFrameKind.JournalBatch => ReadJournalBatchFrame(ref payload),
            BrokerFrameKind.StartWatch => BrokerFrame.StartWatch(ReadCursor(ref payload)),
            BrokerFrameKind.CaughtUp => BrokerFrame.CaughtUp(),
            BrokerFrameKind.ScanCompleted => BrokerFrame.ScanCompleted(ReadCursor(ref payload)),
            _ => throw new InvalidDataException($"Unknown frame kind: {kind}")
        };
    }

    // Private read helpers

    static UsnJournalCursor ReadCursor(ref PayloadReader payload)
    {
        var journalId = payload.UInt64();
        return new UsnJournalCursor(journalId, payload.Int64());
    }

    static BrokerFrame ReadArmAndScanFrame(ref PayloadReader payload)
    {
        var sectionName = payload.String();
        var profile = (BrokerScanProfile)payload.Int32();
        if (!Enum.IsDefined(profile))
        {
            throw new InvalidDataException($"Unknown broker scan profile: {(int)profile}");
        }

        var nameCount = payload.Count(minimumItemBytes: 4);
        var keepFileNames = new List<string>(nameCount);
        while (keepFileNames.Count < nameCount)
        {
            keepFileNames.Add(payload.String());
        }

        return BrokerFrame.ArmAndScan(sectionName, profile, keepFileNames);
    }

    static BrokerFrame ReadJournalBatchFrame(ref PayloadReader payload)
    {
        var cursor = ReadCursor(ref payload);
        var entryCount = payload.EntryCount();
        var entries = new List<UsnJournalEntry>(entryCount);
        while (entries.Count < entryCount)
        {
            entries.Add(payload.Entry());
        }

        return BrokerFrame.JournalBatch(cursor, entries.ToArray());
    }

    static BrokerFrame ReadCatchUpLostFrame(ref PayloadReader payload)
    {
        var cause = (JournalCheckpointLossCause)payload.Int32();
        if (!Enum.IsDefined(cause))
        {
            throw new InvalidDataException($"Unknown checkpoint loss cause: {(int)cause}");
        }

        var loss = new BrokerCatchUpLoss(cause, payload.Int64(), payload.Int64(), payload.Int64(),
            payload.Int64(), payload.Int64(), payload.NullableInt64(), payload.NullableInt64());
        return BrokerFrame.CatchUpLost(loss);
    }

    static BrokerScanProgress ReadScanProgress(ref PayloadReader payload)
    {
        var phaseRaw = payload.Int32();
        var phase = (BrokerScanPhase)phaseRaw;
        if (phaseRaw < byte.MinValue || phaseRaw > byte.MaxValue || !Enum.IsDefined(phase))
        {
            throw new InvalidDataException($"Unknown broker scan phase: {phaseRaw}");
        }

        var recordsProcessed = payload.Int64();
        var bytesProcessed = payload.Int64();
        var totalRecordsRaw = payload.Int64();
        var totalBytesRaw = payload.Int64();
        var elapsedTicks = payload.Int64();

        return new BrokerScanProgress
        {
            DriveLetter = string.Empty,
            Phase = phase,
            RecordsProcessed = recordsProcessed,
            BytesProcessed = bytesProcessed,
            TotalRecords = totalRecordsRaw >= 0 ? totalRecordsRaw : null,
            TotalBytes = totalBytesRaw >= 0 ? totalBytesRaw : null,
            Elapsed = TimeSpan.FromTicks(elapsedTicks)
        };
    }
}
