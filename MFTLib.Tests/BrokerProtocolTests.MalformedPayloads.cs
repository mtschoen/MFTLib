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
        BrokerProtocol.WriteJournalBatch(buffer, new UsnJournalCursor(7, 100),
        [
            UsnJournalEntry.Create(new UsnJournalEntryOptions
            {
                RecordNumber = 1,
                ParentRecordNumber = 5,
                Usn = 10,
                Reason = UsnReason.FileCreate,
                FileAttributes = FileAttributes.Normal,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FileName = fileName
            })
        ]);
        return buffer.WrittenSpan.ToArray();
    }

    [TestMethod]
    public void ReadFrame_FixedFieldPastTheEndOfThePayload_ThrowsInvalidDataExceptionNamingTheShortfall()
    {
        var frame = FrameWithPayload(BrokerFrameKind.ChannelOpened, [1, 2]);

        var exception = Assert.ThrowsException<InvalidDataException>(() => BrokerProtocol.ReadFrame(frame, out _));

        StringAssert.Contains(exception.Message, "4 bytes needed, 2 left");
    }

    [TestMethod]
    public void ReadFrame_JournalEntryWhoseNameLengthPointsPastThePayload_ThrowsInvalidDataException()
    {
        var frame = BatchFrameWithOneEntry("a.txt");
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(NameLengthOffset), int.MaxValue);

        var exception = Assert.ThrowsException<InvalidDataException>(() => BrokerProtocol.ReadFrame(frame, out _));

        StringAssert.Contains(exception.Message, "journal entry declares");
    }

    [TestMethod]
    public void ReadFrame_JournalEntryWithNegativeNameLength_ThrowsInvalidDataException()
    {
        var frame = BatchFrameWithOneEntry("a.txt");
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(NameLengthOffset), -1);

        Assert.ThrowsException<InvalidDataException>(() => BrokerProtocol.ReadFrame(frame, out _));
    }

    [TestMethod]
    public void ReadFrame_JournalBatchCountingAnEntryTheNamesLeftNoRoomFor_ThrowsInvalidDataException()
    {
        // One entry whose name is exactly one fixed part long: the payload then holds two fixed
        // parts, so a count of two passes the count check, but the first entry takes every byte.
        var frame = BatchFrameWithOneEntry(new string('n', EntryFixedBytes / 2));
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(5 + 16), 2);

        var exception = Assert.ThrowsException<InvalidDataException>(() => BrokerProtocol.ReadFrame(frame, out _));

        StringAssert.Contains(exception.Message, $"{EntryFixedBytes} bytes needed, 0 left");
    }
}
