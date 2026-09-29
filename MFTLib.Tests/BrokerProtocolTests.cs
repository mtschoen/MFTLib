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
    static readonly string[] KeepFileNamesGitAndNonAscii = [".git", "repört"];
    static readonly string[] KeepFileNamesGit = [".git"];
    static readonly string[] KeepFileNamesSingleLetter = ["a"];

    static void AssertWireBytes(Action<ArrayBufferWriter<byte>> write, byte[] expected)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        CollectionAssert.AreEqual(expected, buffer.WrittenSpan.ToArray());
    }

    // Writes one frame, reads it back, and checks the reader consumed exactly the bytes written.
    static BrokerFrame RoundTrip(Action<ArrayBufferWriter<byte>> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);
        Assert.AreEqual(buffer.WrittenCount, consumed);
        return frame;
    }

    [TestMethod]
    public void ReadFrame_SetsConsumedToFullFrameLength()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteError(buffer, 1, "test");
        BrokerProtocol.WriteHeartbeat(buffer);

        BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(buffer.WrittenCount - 5, consumed, "The reader stops at the end of the first frame.");
    }

    [DataTestMethod]
    [DataRow((byte)0)]
    [DataRow((byte)18)]
    [DataRow((byte)99)]
    public void ReadFrame_UnknownKind_ThrowsInvalidDataException(byte kind)
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteHeartbeat(buffer); // any no-payload frame gives the right shape
        var bytes = buffer.WrittenSpan.ToArray();
        bytes[4] = kind; // a value no BrokerFrameKind defines

        Assert.ThrowsException<InvalidDataException>(() => BrokerProtocol.ReadFrame(bytes, out _));
    }

    [TestMethod]
    public void WireTable_NumbersKindsDenselyFromOne()
    {
        var kinds = Enum.GetValues<BrokerFrameKind>().Select(kind => (int)kind).Order().ToArray();

        CollectionAssert.AreEqual(Enumerable.Range(1, 17).ToArray(), kinds, "The wire numbers kinds densely from 1.");
    }

    // The Require* accessors guard a protocol invariant: every factory receives a real, non-null
    // string, so a decoded frame never has these null. default(BrokerFrame) has null string
    // fields, so it exercises the violated-invariant branch without a public path to build an
    // otherwise-invalid frame.

    [TestMethod]
    public void RequireDrive_NullDrive_ThrowsInvalidDataException()
    {
        Assert.ThrowsException<InvalidDataException>(() => default(BrokerFrame).RequireDrive());
    }

    [TestMethod]
    public void RequirePipeName_NullPipeName_ThrowsInvalidDataException()
    {
        Assert.ThrowsException<InvalidDataException>(() => default(BrokerFrame).RequirePipeName());
    }

    [TestMethod]
    public void RequireSectionName_NullSectionName_ThrowsInvalidDataException()
    {
        Assert.ThrowsException<InvalidDataException>(() => default(BrokerFrame).RequireSectionName());
    }

    [TestMethod]
    public void RequireMessage_NullMessage_ThrowsInvalidDataException()
    {
        Assert.ThrowsException<InvalidDataException>(() => default(BrokerFrame).RequireMessage());
    }

    [TestMethod]
    public void RequireCatchUpLoss_FrameWithoutLoss_ThrowsInvalidDataException()
    {
        Assert.ThrowsException<InvalidDataException>(() => default(BrokerFrame).RequireCatchUpLoss('C'));
    }
}
