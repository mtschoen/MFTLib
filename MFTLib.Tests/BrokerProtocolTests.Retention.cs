using System.Buffers;
using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProtocolTests
{
    [TestMethod]
    [DataRow(null, false, 0)]
    [DataRow(new string[0], true, 0)]
    [DataRow(new[] { "a" }, true, 1)]
    public void ArmAndScan_NullableRetention_RoundTripsAndMatchesGoldenBytes(
        string[]? names, bool expectedPresent, int expectedCount)
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(buffer, BrokerFrame.ArmAndScan("C", names));
        byte[] expected = expectedCount == 1
            ? [19, 0, 0, 0, 10, 2, 0, 0, 0, 67, 0, 0, 1, 1, 0, 0, 0, 2, 0, 0, 0, 97, 0]
            : [13, 0, 0, 0, 10, 2, 0, 0, 0, 67, 0, 0, expectedPresent ? (byte)1 : (byte)0, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(13), expectedCount);
        Assert.AreSequenceEqual(expected, [.. buffer.WrittenSpan]);
        Assert.AreEqual((long)buffer.WrittenCount - 4, BrokerProtocol.ArmAndScanFrameLength("C", names));
        var frame = RoundTrip(BrokerFrame.ArmAndScan("C", names), BrokerProtocol.WriteArmAndScan);
        if (names is null)
        {
            Assert.IsNull(frame.RetainedFileNames);
        }
        else
        {
            Assert.AreSequenceEqual(names, [.. frame.RetainedFileNames!]);
        }
    }

    [TestMethod]
    [DataRow(true, -1, (byte)0, 0)]
    [DataRow(true, -2, (byte)0, 0)]
    [DataRow(true, int.MaxValue, (byte)0, 0)]
    [DataRow(true, 1, (byte)0, 0)]
    [DataRow(true, 1, (byte)0, 4)]
    [DataRow(false, 0, (byte)2, 0)]
    [DataRow(false, 0, (byte)0, 4)]
    [DataRow(true, 0, (byte)0, 4)]
    public void ArmAndScan_MalformedRetentionPayload_IsRejected(
        bool present, int count, byte freedFlag, int trailingBytes)
    {
        var bytes = new byte[17 + trailingBytes];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, bytes.Length - 4);
        bytes[4] = 10;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), 2);
        bytes[9] = 67;
        bytes[11] = freedFlag;
        bytes[12] = present ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(13), count);
        if (count == 1 && trailingBytes == 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(17), 2);
        }
        Assert.ThrowsExactly<InvalidDataException>(() => BrokerProtocol.ReadFrame(bytes, out _));
    }
}
