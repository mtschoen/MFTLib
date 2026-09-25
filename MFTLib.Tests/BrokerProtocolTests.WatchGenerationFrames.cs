using System.Buffers;
using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The watch generation is what keeps a late EndWatchAck from ending the next watch, so a frame
///     laid out without one (a client and broker from different builds) must be refused with a clear
///     message rather than read as some default generation.
/// </summary>
public partial class BrokerProtocolTests
{
    [DataTestMethod]
    [DataRow((byte)BrokerFrameKind.EndWatch)]
    [DataRow((byte)BrokerFrameKind.EndWatchAck)]
    public void ReadFrame_WatchGenerationFrameWithoutAGeneration_IsRejectedAsABuildMismatch(byte kind)
    {
        byte[] frameWithoutGeneration = [0x01, 0x00, 0x00, 0x00, kind];

        var exception = Assert.ThrowsException<InvalidDataException>(
            () => BrokerProtocol.ReadFrame(frameWithoutGeneration, out _));

        StringAssert.Contains(exception.Message, ((BrokerFrameKind)kind).ToString());
        StringAssert.Contains(exception.Message, "different MFTLib builds");
    }

    [TestMethod]
    public void ReadFrame_StartWatchWithoutAGeneration_IsRejectedAsABuildMismatch()
    {
        // An empty drives spec's length prefix and nothing else: the layout without a generation.
        byte[] frameWithoutGeneration =
            [0x05, 0x00, 0x00, 0x00, (byte)BrokerFrameKind.StartWatch, 0x00, 0x00, 0x00, 0x00];

        var exception = Assert.ThrowsException<InvalidDataException>(
            () => BrokerProtocol.ReadFrame(frameWithoutGeneration, out _));

        StringAssert.Contains(exception.Message, nameof(BrokerFrameKind.StartWatch));
        StringAssert.Contains(exception.Message, "different MFTLib builds");
    }

    [TestMethod]
    public void ReadFrame_StartWatchGenerationZero_IsRejected()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteStartWatch(buffer, BrokerFrame.NoWatchGeneration, "C:7:100:1");

        AssertGenerationZeroRejected(buffer);
    }

    [TestMethod]
    public void ReadFrame_EndWatchGenerationZero_IsRejected()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatch(buffer, BrokerFrame.NoWatchGeneration);

        AssertGenerationZeroRejected(buffer);
    }

    [TestMethod]
    public void ReadFrame_EndWatchAckGenerationZero_IsRejected()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteEndWatchAck(buffer, BrokerFrame.NoWatchGeneration);

        AssertGenerationZeroRejected(buffer);
    }

    [TestMethod]
    public void ReadFrame_EndWatchAckWithTrailingBytes_IsRejectedAsABuildMismatch()
    {
        var frame = new byte[4 + 1 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 9);
        frame[4] = (byte)BrokerFrameKind.EndWatchAck;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(5), 1);

        var exception = Assert.ThrowsException<InvalidDataException>(() => BrokerProtocol.ReadFrame(frame, out _));

        StringAssert.Contains(exception.Message, "different MFTLib builds");
    }

    static void AssertGenerationZeroRejected(ArrayBufferWriter<byte> buffer)
    {
        var exception = Assert.ThrowsException<InvalidDataException>(
            () => BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _));

        StringAssert.Contains(exception.Message, "watch generation 0");
    }
}
