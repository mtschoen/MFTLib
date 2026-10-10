using System.Buffers;
using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProtocolTests
{
    [TestMethod]
    [DataRow(null, -1)]
    [DataRow(new string[0], 0)]
    [DataRow(new[] { "a" }, 1)]
    public void ArmAndScan_NullableRetention_RoundTripsAndMatchesGoldenBytes(string[]? names, int count)
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(buffer, "C", names);
        byte[] expected = count == 1
            ? [21, 0, 0, 0, 10, 2, 0, 0, 0, 67, 0, 0, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 97, 0]
            : [15, 0, 0, 0, 10, 2, 0, 0, 0, 67, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(15), count);
        CollectionAssert.AreEqual(expected, buffer.WrittenSpan.ToArray());
        Assert.AreEqual((long)buffer.WrittenCount - 4, BrokerProtocol.ArmAndScanFrameLength("C", names));
        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _);
        if (names is null)
        {
            Assert.IsNull(frame.DirectoryScanFileNames);
        }
        else
        {
            CollectionAssert.AreEqual(names, frame.DirectoryScanFileNames!.ToArray());
        }
    }

    [TestMethod]
    [DataRow(-2, 0, 0)]
    [DataRow(int.MaxValue, 0, 0)]
    [DataRow(1, 0, 0)]
    [DataRow(1, 0, 4)]
    [DataRow(-1, 2, 0)]
    [DataRow(-1, 0, 4)]
    [DataRow(0, 0, 4)]
    public void ArmAndScan_MalformedRetentionPayload_IsRejected(int count, int freedFlag, int trailingBytes)
    {
        byte[] bytes = new byte[19 + trailingBytes];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, bytes.Length - 4);
        bytes[4] = 10;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5), 2);
        bytes[9] = 67;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(11), freedFlag);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(15), count);
        if (count == 1 && trailingBytes == 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(19), 2);
        }
        Assert.ThrowsException<InvalidDataException>(() => BrokerProtocol.ReadFrame(bytes, out _));
    }
}
