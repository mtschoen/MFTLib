using System.Buffers;
using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Frames whose length prefix is right but whose payload the kind's reader cannot decode.
public partial class BrokerProtocolTests
{
    // Bytes of the fixed part of one journal entry, and where its name length sits in a batch frame:
    // the frame prefix and kind (5), the cursor (16), the entry count (4), then the entry's fixed part.
    const int EntryFixedBytes = 46;
    const int NameLengthOffset = 5 + 16 + 4 + EntryFixedBytes - 4;

    static byte[] FrameWithPayload(BrokerFrameKind kind, byte[] payload)
    {
        var frame = new byte[5 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 1 + payload.Length);
        frame[4] = (byte)kind;
        payload.CopyTo(frame, 5);
        return frame;
    }

    static byte[] BatchFrameWithOneEntry(string fileName)
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteJournalBatch(buffer, BrokerFrame.JournalBatch(new UsnJournalCursor(7, 100),
        [
            UsnJournalEntry.Create(new UsnJournalEntryOptions
            {
                RecordNumber = 1,
                ParentRecordNumber = 5,
                Usn = 10,
                Reason = UsnReason.FileCreate,
                FileAttributes = FileAttributes.Normal,
                TimestampUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FileName = fileName
            })
        ]));
        return [.. buffer.WrittenSpan];
    }

    static byte[] ValidFrame(BrokerFrameKind kind)
    {
        if (kind == BrokerFrameKind.JournalBatch)
        {
            return BatchFrameWithOneEntry("a.txt");
        }

        var buffer = new ArrayBufferWriter<byte>();
        switch (kind)
        {
            case BrokerFrameKind.OpenChannel:
                BrokerProtocol.WriteOpenChannel(buffer, BrokerFrame.OpenChannel(1, 'C', "p"));
                break;
            case BrokerFrameKind.ChannelOpened:
                BrokerProtocol.WriteChannelOpened(buffer, BrokerFrame.ChannelOpened(1));
                break;
            case BrokerFrameKind.QueryVolume:
                BrokerProtocol.WriteQueryVolume(buffer, BrokerFrame.QueryVolume(1, 'C'));
                break;
            case BrokerFrameKind.VolumeInfo:
                BrokerProtocol.WriteVolumeInfo(buffer, BrokerFrame.VolumeInfo(1, 1024, 4096));
                break;
            case BrokerFrameKind.GrowUsnJournal:
                BrokerProtocol.WriteGrowUsnJournal(buffer, BrokerFrame.GrowUsnJournal(1, 'C', 0x08000000, 0x01000000));
                break;
            case BrokerFrameKind.UsnJournalSettings:
                BrokerProtocol.WriteUsnJournalSettings(buffer, BrokerFrame.UsnJournalSettings(1, 0x08000000, 0x01000000));
                break;
            case BrokerFrameKind.Error:
                BrokerProtocol.WriteError(buffer, BrokerFrame.Error(1, "e"));
                break;
            case BrokerFrameKind.Heartbeat:
                BrokerProtocol.WriteHeartbeat(buffer, BrokerFrame.Heartbeat());
                break;
            case BrokerFrameKind.Stalled:
                BrokerProtocol.WriteStalled(buffer, BrokerFrame.Stalled("s"));
                break;
            case BrokerFrameKind.ArmAndScan:
                BrokerProtocol.WriteArmAndScan(buffer, BrokerFrame.ArmAndScan("s", RetainedFileNamesGitAndNonAscii, includeFreed: true));
                break;
            case BrokerFrameKind.Cursor:
                BrokerProtocol.WriteCursor(buffer, BrokerFrame.ArmedCursor(new UsnJournalCursor(1, 2)));
                break;
            case BrokerFrameKind.ScanProgress:
                BrokerProtocol.WriteScanProgress(buffer, BrokerFrame.ScanProgress(new BrokerScanProgress
                {
                    Phase = BrokerScanPhase.Parsing,
                    RecordsProcessed = 1,
                    BytesProcessed = 2,
                    TotalRecords = 3,
                    TotalBytes = 4
                }));
                break;
            case BrokerFrameKind.CatchUpLost:
                BrokerProtocol.WriteCatchUpLost(buffer, BrokerFrame.CatchUpLost(FullLoss));
                break;
            case BrokerFrameKind.ScanReady:
                BrokerProtocol.WriteScanReady(buffer, BrokerFrame.ScanReady(1));
                break;
            case BrokerFrameKind.StartWatch:
                BrokerProtocol.WriteStartWatch(buffer, BrokerFrame.StartWatch(new UsnJournalCursor(1, 2)));
                break;
            case BrokerFrameKind.CaughtUp:
                BrokerProtocol.WriteCaughtUp(buffer, BrokerFrame.CaughtUp());
                break;
            case BrokerFrameKind.ScanCompleted:
                BrokerProtocol.WriteScanCompleted(buffer, BrokerFrame.ScanCompleted());
                break;
        }

        return [.. buffer.WrittenSpan];
    }

    [TestMethod]
    [DataRow((int)BrokerFrameKind.OpenChannel)]
    [DataRow((int)BrokerFrameKind.ChannelOpened)]
    [DataRow((int)BrokerFrameKind.QueryVolume)]
    [DataRow((int)BrokerFrameKind.VolumeInfo)]
    [DataRow((int)BrokerFrameKind.GrowUsnJournal)]
    [DataRow((int)BrokerFrameKind.UsnJournalSettings)]
    [DataRow((int)BrokerFrameKind.Error)]
    [DataRow((int)BrokerFrameKind.Heartbeat)]
    [DataRow((int)BrokerFrameKind.Stalled)]
    [DataRow((int)BrokerFrameKind.ArmAndScan)]
    [DataRow((int)BrokerFrameKind.Cursor)]
    [DataRow((int)BrokerFrameKind.ScanProgress)]
    [DataRow((int)BrokerFrameKind.CatchUpLost)]
    [DataRow((int)BrokerFrameKind.ScanReady)]
    [DataRow((int)BrokerFrameKind.JournalBatch)]
    [DataRow((int)BrokerFrameKind.StartWatch)]
    [DataRow((int)BrokerFrameKind.CaughtUp)]
    [DataRow((int)BrokerFrameKind.ScanCompleted)]
    public void ReadFrame_TrailingPayloadByte_ThrowsInvalidDataException(int kindValue)
    {
        var kind = (BrokerFrameKind)kindValue;
        var bytes = ValidFrame(kind);

        var decoded = BrokerProtocol.ReadFrame(bytes, out var consumed);
        Assert.AreEqual(kind, decoded.Kind);
        Assert.AreEqual(bytes.Length, consumed);

        byte[] trailing = [.. bytes, 0xFF];
        var declaredLength = BinaryPrimitives.ReadInt32LittleEndian(trailing);
        BinaryPrimitives.WriteInt32LittleEndian(trailing, declaredLength + 1);

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(trailing, out _));
        Assert.Contains("trailing bytes", exception.Message);
    }

    [TestMethod]
    public void ReadFrame_FixedFieldPastTheEndOfThePayload_ThrowsInvalidDataExceptionNamingTheShortfall()
    {
        var frame = FrameWithPayload(BrokerFrameKind.ChannelOpened, [1, 2]);

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(frame, out _));

        Assert.Contains("4 bytes needed, 2 left", exception.Message);
    }

    [TestMethod]
    public void ReadFrame_JournalEntryWhoseNameLengthPointsPastThePayload_ThrowsInvalidDataException()
    {
        var frame = BatchFrameWithOneEntry("a.txt");
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(NameLengthOffset), int.MaxValue);

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(frame, out _));

        Assert.Contains("journal entry declares", exception.Message);
    }

    [TestMethod]
    public void ReadFrame_JournalEntryWithNegativeNameLength_ThrowsInvalidDataException()
    {
        var frame = BatchFrameWithOneEntry("a.txt");
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(NameLengthOffset), -1);

        Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(frame, out _));
    }

    [TestMethod]
    [DataRow(2, "46 bytes needed, 0 left")]
    [DataRow(-1, "declares -1 items")]
    [DataRow(int.MaxValue, "declares 2147483647 items")]
    public void ReadFrame_JournalBatchWithAnInvalidEntryCount_ThrowsInvalidDataException(int count, string expectedMessage)
    {
        // One entry whose name is exactly one fixed part long: the payload then holds two fixed
        // parts, so a count of two passes the count check, but the first entry takes every byte.
        var frame = BatchFrameWithOneEntry(new string('n', EntryFixedBytes / 2));
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(5 + 16), count);

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(frame, out _));

        Assert.Contains(expectedMessage, exception.Message);
    }

    [TestMethod]
    [DataRow((int)BrokerFrameKind.OpenChannel)]
    [DataRow((int)BrokerFrameKind.QueryVolume)]
    [DataRow((int)BrokerFrameKind.GrowUsnJournal)]
    public void ReadFrame_ControlDriveRejectsMalformedPayload(int kindValue)
    {
        var kind = (BrokerFrameKind)kindValue;
        var suffix = kind switch
        {
            BrokerFrameKind.OpenChannel => [2, 0, 0, 0, (byte)'p', 0],
            BrokerFrameKind.GrowUsnJournal => new byte[16],
            _ => []
        };
        foreach (var character in (ReadOnlySpan<char>)['\0', '1', ':', '\u00e9', 'c'])
        {
            byte[] payload = [1, 0, 0, 0, (byte)character, (byte)(character >> 8), .. suffix];
            var frame = FrameWithPayload(kind, payload);
            Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(frame, out _));
        }
        byte[][] invalidCharacterBytes = [[], [(byte)'C']];
        foreach (var characterBytes in invalidCharacterBytes)
        {
            var frame = FrameWithPayload(kind, [1, 0, 0, 0, .. characterBytes]);
            Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(frame, out _));
        }
        // A valid C character followed by path-shaped trailing bytes is not a drive request.
        var trailing = FrameWithPayload(kind, [1, 0, 0, 0, (byte)'C', 0, .. suffix, (byte)':', 0, (byte)'\\', 0]);
        Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(trailing, out _));
        // Old length-prefixed string payloads must not survive as an alternate grammar.
        foreach (var text in new[] { "C", @"C:\", @"\\.\C:" })
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(text);
            var payload = new byte[4 + 4 + bytes.Length + suffix.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), bytes.Length);
            bytes.CopyTo(payload, 8);
            suffix.CopyTo(payload, 8 + bytes.Length);
            var frame = FrameWithPayload(kind, payload);
            Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(frame, out _));
        }
    }
}
