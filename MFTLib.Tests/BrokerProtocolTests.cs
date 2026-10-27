using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The broker wire: every frame kind round-trips through its writer and reader, the golden
///     bytes pin the layout, and a frame the reader cannot decode fails as
///     <see cref="InvalidDataException" />. Control-pipe kinds are in the Frames partial,
///     drive-pipe kinds in the Scan partial, the journal grow pair in the GrowUsnJournalFrames partial.
/// </summary>
[TestClass]
public partial class BrokerProtocolTests
{
    static readonly string[] RetainedFileNamesGitAndNonAscii = [".git", "repört"];
    static readonly string[] RetainedFileNamesSingleLetter = ["a"];

    static void AssertWireBytes(Action<ArrayBufferWriter<byte>> write, byte[] expected)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        Assert.AreSequenceEqual(expected, [.. buffer.WrittenSpan]);
    }

    // Writes one frame from its expected model, reads it back, and checks every field and the byte count.
    static BrokerFrame RoundTrip(BrokerFrame expected, Action<IBufferWriter<byte>, BrokerFrame> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer, expected);
        var actual = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);
        Assert.AreEqual(buffer.WrittenCount, consumed);
        AssertFrameEqual(expected, actual);
        return actual;
    }

    static void AssertFrameEqual(BrokerFrame expected, BrokerFrame actual)
    {
        Assert.AreEqual(expected.Kind, actual.Kind);
        Assert.AreEqual(expected.RequestId, actual.RequestId);
        Assert.AreEqual(expected.Drive, actual.Drive);
        Assert.AreEqual(expected.PipeName, actual.PipeName);
        Assert.AreEqual(expected.SectionName, actual.SectionName);
        Assert.AreEqual(expected.IncludeFreed, actual.IncludeFreed);
        Assert.AreEqual(expected.Cursor, actual.Cursor);
        Assert.AreEqual(expected.SkippedRecordCount, actual.SkippedRecordCount);
        Assert.AreEqual(expected.Message, actual.Message);
        Assert.AreEqual(expected.Progress, actual.Progress);
        Assert.AreEqual(expected.BytesPerFileRecordSegment, actual.BytesPerFileRecordSegment);
        Assert.AreEqual(expected.MftValidDataLength, actual.MftValidDataLength);
        Assert.AreEqual(expected.JournalMaximumSize, actual.JournalMaximumSize);
        Assert.AreEqual(expected.JournalAllocationDelta, actual.JournalAllocationDelta);
        Assert.AreEqual(expected.CatchUpLoss, actual.CatchUpLoss);
        if (expected.RetainedFileNames is null)
        {
            Assert.IsNull(actual.RetainedFileNames);
        }
        else
        {
            Assert.IsNotNull(actual.RetainedFileNames);
            Assert.AreSequenceEqual([.. expected.RetainedFileNames], [.. actual.RetainedFileNames]);
        }

        Assert.HasCount(expected.Entries.Length, actual.Entries);
        for (var index = 0; index < expected.Entries.Length; index++)
        {
            var expectedEntry = expected.Entries[index];
            var actualEntry = actual.Entries[index];
            Assert.AreEqual(expectedEntry.RecordNumber, actualEntry.RecordNumber);
            Assert.AreEqual(expectedEntry.ParentRecordNumber, actualEntry.ParentRecordNumber);
            Assert.AreEqual(expectedEntry.SequenceNumber, actualEntry.SequenceNumber);
            Assert.AreEqual(expectedEntry.Usn, actualEntry.Usn);
            Assert.AreEqual(expectedEntry.TimestampUtc, actualEntry.TimestampUtc);
            Assert.AreEqual(expectedEntry.Reason, actualEntry.Reason);
            Assert.AreEqual(expectedEntry.FileAttributes, actualEntry.FileAttributes);
            Assert.AreEqual(expectedEntry.FileName, actualEntry.FileName);
        }
    }

    [TestMethod]
    public void ReadFrame_SetsConsumedToFullFrameLength()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(buffer, BrokerFrame.Error(1, "test"));
        BrokerProtocol.WriteHeartbeat(buffer, BrokerFrame.Heartbeat());

        var firstFrame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.Error, firstFrame.Kind);
        Assert.AreEqual(buffer.WrittenCount - 5, consumed, "The reader stops at the end of the first frame.");

        var secondFrame = BrokerProtocol.ReadFrame(buffer.WrittenSpan[consumed..], out var secondConsumed);

        Assert.AreEqual(BrokerFrameKind.Heartbeat, secondFrame.Kind);
        Assert.AreEqual(5, secondConsumed);
    }

    [TestMethod]
    [DataRow((byte)0)]
    [DataRow((byte)19)]
    [DataRow((byte)99)]
    public void ReadFrame_UnknownKind_ThrowsInvalidDataException(byte kind)
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteHeartbeat(buffer, BrokerFrame.Heartbeat()); // any no-payload frame gives the right shape
        var bytes = buffer.WrittenSpan.ToArray();
        bytes[4] = kind; // a value no BrokerFrameKind defines

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(bytes, out _));
        Assert.AreEqual($"Unknown frame kind: {kind}", exception.Message);
    }

    [TestMethod]
    public void WireTable_NumbersKindsDenselyFromOne()
    {
        var kinds = Enum.GetValues<BrokerFrameKind>().Select(kind => (int)kind).Order().ToArray();

        Assert.AreSequenceEqual([.. Enumerable.Range(1, 18)], kinds, "The wire numbers kinds densely from 1.");
    }

    // The Require* accessors guard a protocol invariant: every factory receives a real, non-null
    // string, so a decoded frame never has these null. default(BrokerFrame) has null string
    // fields, so it exercises the violated-invariant branch without a public path to build an
    // otherwise-invalid frame.

    [TestMethod]
    public void RequireDrive_NullDrive_ThrowsInvalidDataException()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => default(BrokerFrame).RequireDrive());
    }

    [TestMethod]
    public void RequirePipeName_NullPipeName_ThrowsInvalidDataException()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => default(BrokerFrame).RequirePipeName());
    }

    [TestMethod]
    public void RequireSectionName_NullSectionName_ThrowsInvalidDataException()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => default(BrokerFrame).RequireSectionName('C'));
    }

    [TestMethod]
    public void RequireMessage_NullMessage_ThrowsInvalidDataException()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => default(BrokerFrame).RequireMessage());
    }

    [TestMethod]
    public void RequireCatchUpLoss_FrameWithoutLoss_ThrowsInvalidDataException()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => default(BrokerFrame).RequireCatchUpLoss('C'));
    }
}
