using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The serial of the block the client creates is the serial the host is asked to verify.
public partial class BrokerProcessTests
{
    [TestMethod]
    public async Task ScanVolume_SendsTheBlocksStampedSerialToTheHostsScanHandleCheck()
    {
        ulong? asked = null;
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ScanOpened = (_, serial) => asked = serial
        }));
        var target = TestBlockSections.Target() with { VolumeSerial = 0x1111111122222222UL };

        var result = await broker.Process.ScanVolumeAsync('C', target, new MftScanOptions(), null, CancellationToken.None)
            .WaitAsync(HangGuard, TestContext.CancellationToken);
        using var block = result.Block.Block;

        Assert.AreEqual(0x1111111122222222UL, asked);
        Assert.AreEqual(0x1111111122222222UL, block.Header.VolumeSerial);
    }

    [TestMethod]
    public async Task ScanVolume_WhenTheHostRefusesTheSerial_FailsTheScanWithTheHostsMessageAndReleasesTheSection()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            ScanOpened = (volume, _) => throw new IOException($"Volume {volume} does not have the expected serial.")
        }));
        var target = TestBlockSections.Target() with { VolumeSerial = 9 };

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            broker.Process.ScanVolumeAsync('C', target, new MftScanOptions(), null, CancellationToken.None)
                .WaitAsync(HangGuard, TestContext.CancellationToken));

        Assert.AreEqual("Volume C does not have the expected serial.", exception.Message);
        Assert.IsTrue(broker.Sections.Single().Lifetime.IsDisposed);
    }
}
