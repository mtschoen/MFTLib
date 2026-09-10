using System.Buffers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

public partial class BrokerProtocolTests
{
    [TestMethod]
    public void DisarmDriveFrame_RoundTripsItsDrive()
    {
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteDisarmDrive(buffer, "D:\\");

        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);

        Assert.AreEqual(BrokerFrameKind.DisarmDrive, frame.Kind);
        Assert.AreEqual("D:\\", frame.RequireDrive());
        Assert.AreEqual(buffer.WrittenCount, consumed);
    }

    [TestMethod]
    public void WireBytes_Golden_DisarmDriveFrame()
    {
        AssertWireBytes(w => BrokerProtocol.WriteDisarmDrive(w, "C"),
            [0x07, 0x00, 0x00, 0x00, 0x0F, 0x02, 0x00, 0x00, 0x00, 0x43, 0x00]);
    }
}
