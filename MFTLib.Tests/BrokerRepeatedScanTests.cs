using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public sealed class BrokerRepeatedScanTests
{
    [TestMethod]
    [DataRow(true, DisplayName = "uncached blocks")]
    [DataRow(false, DisplayName = "cached blocks")]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task OneSession_ThreeNamedSectionScans_CompleteWithReadableRows(bool deleteOnClose)
    {
        using var directory = new TempDirectory("mftlib-repeated-broker");
        var armed = new UsnJournalCursor(7, 1000);
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => armed,
            ScanVolume = (_, _, _, _, _, _) =>
                [[BrokerHostFactory.Record(5, ".", 3), BrokerHostFactory.Record(20, "file.txt")]]
        });
        await using var harness = new HostChannelHarness(host, new RealBlockSectionWriter());

        for (var iteration = 0; iteration < 3; iteration++)
        {
            var sectionName = NamedBlockSection.BuildSectionName('T');
            var path = Path.Combine(directory.Path, $"block-{iteration}.bin");
            var (block, lifetime) = NamedBlockSection.Create(
                TestBlocks.Options(path, ProducerKind.Mft, 128, 1024, rootRow: 5,
                    deleteOnClose: deleteOnClose), sectionName);
            using (block)
            using (lifetime)
            {
                var pipe = await harness.OpenChannelAsync('T');
                await HostChannelHarness.WriteFrameAsync(pipe, writer => BrokerProtocol.WriteArmAndScan(writer,
                    BrokerFrame.ArmAndScan(sectionName, includeFreed: false, deleteOnClose: false)));
                var frames = await HostChannelHarness.ReadToEndAsync(pipe);

                Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
                Assert.AreEqual(armed, frames[0].Cursor);
                Assert.AreEqual(BrokerFrameKind.ScanReady, frames[^2].Kind, $"Scan {iteration}: {frames[^1].Message}");
                Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind, $"Scan {iteration}: {frames[^1].Message}");
                Assert.IsTrue(block.Header.IsComplete);
                var published = block.Header;
                Assert.IsNull(BlockHeader.ValidateSection(in published, block.Length));
                Assert.IsTrue(block.TryAdoptPublishedCursors(published));
                Assert.AreEqual(2u, block.Header.LiveRowCount);
                Assert.AreEqual("file.txt", NamePool.ReadRowName(block, 20).ToString());
            }
        }

        Assert.IsFalse(harness.Serve.IsCompleted, "All three scans leave the control session alive.");
    }
}
