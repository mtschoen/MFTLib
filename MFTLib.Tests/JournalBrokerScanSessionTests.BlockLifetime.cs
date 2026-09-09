using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The session owns the blocks of the scan result it holds. These tests observe disposal
// through the backing file rather than through a flag: every block a session test creates
// is a DeleteOnClose block, so BlockFile.Dispose deletes the file and File.Exists reports
// the outcome without widening BlockFile's surface for the sake of a test.
public partial class JournalBrokerScanSessionTests
{
    [TestMethod]
    public async Task Rescan_SecondScan_DisposesSupersededBlocksAndLeavesTheNewOnesLive()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var initialScanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC,
            CreateOptions(), cancellationToken: CancellationToken.None);
        await initialScanTask;

        var supersededBlockPath = session.LatestScan!.BlockOutcomes["C"].Block.Path;
        Assert.IsTrue(File.Exists(supersededBlockPath), "the first scan's block should be live before the rescan");

        var rescanBrokerTask = RespondToArmAndScanAsync(serverSide, "C");
        await session.RescanAsync(CreateOptions(session.Profile));
        await rescanBrokerTask;

        var publishedBlockPath = session.LatestScan!.BlockOutcomes["C"].Block.Path;
        Assert.AreNotEqual(supersededBlockPath, publishedBlockPath,
            "each scan writes its own block file, so the two paths must differ for this test to mean anything");
        Assert.IsFalse(File.Exists(supersededBlockPath),
            "the rescan must dispose the blocks of the result it replaced");
        Assert.IsTrue(File.Exists(publishedBlockPath),
            "the newly published result's blocks must stay live");

        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task DisposeAsync_DisposesTheBlocksOfTheScanTheSessionStillHolds()
    {
        var (clientSide, serverSide) = DuplexStream.CreatePair();
        var client = MakeMinimalFakeClient(clientSide);
        var scanTask = RespondToArmAndScanAsync(serverSide, "C");

        var session = await JournalBrokerScanSession.StartAsync(_ => Task.FromResult(client), DriveC,
            CreateOptions(), cancellationToken: CancellationToken.None);
        await scanTask;

        var blockPath = session.LatestScan!.BlockOutcomes["C"].Block.Path;
        Assert.IsTrue(File.Exists(blockPath), "the scan's block should be live while the session holds it");

        await session.DisposeAsync();

        Assert.IsFalse(File.Exists(blockPath),
            "disposing the session must release the blocks of the scan it was still holding");
    }
}
