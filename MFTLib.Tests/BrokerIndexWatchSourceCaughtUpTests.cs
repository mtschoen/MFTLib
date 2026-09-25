using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerIndexWatchSourceCaughtUpTests
{
    [TestMethod]
    public async Task WatchSource_SurfacesACaughtUpFrameAsADriveCaughtUpItem()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var consumption = ReadItemsAndBreakAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100)], harness.CancellationToken), count: 2);

        var start = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, start.Kind);
        var entry = JournalEntryFactory.Create(1, 105, "c.txt");
        await harness.WriteAsync(response =>
        {
            BrokerProtocol.WriteCaughtUp(response, "C", harness.ArmEpochForDrive(start, 'C'));
            BrokerProtocol.WriteJournalBatch(response, "C", harness.ArmEpochForDrive(start, 'C'),
                new UsnJournalCursor(7, 110), [entry]);
        });

        await harness.AcknowledgeEndWatchAsync();

        var items = await consumption;
        Assert.AreEqual(2, items.Count);
        Assert.IsInstanceOfType<DriveCaughtUp>(items[0]);
        var caughtUp = (DriveCaughtUp)items[0];
        Assert.AreEqual('C', caughtUp.DriveLetter);

        // The marker ends nothing: the arm's batches keep flowing after it.
        Assert.IsInstanceOfType<JournalBatch>(items[1]);
        var batch = (JournalBatch)items[1];
        Assert.AreEqual(110L, batch.NextUsn);
        CollectionAssert.AreEqual(new[] { entry }, batch.Entries.ToArray());
    }

    [TestMethod]
    public async Task WatchSource_AfterAReArm_SurfacesOnlyTheFreshArmsMarker()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var consumption = ReadItemsAndBreakAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100)], harness.CancellationToken), count: 2);

        var firstArm = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, firstArm.Kind);

        await source.ArmDriveAsync(new IndexWatchTarget('C', 7, 500), harness.CancellationToken);
        var secondArm = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, secondArm.Kind);

        // The superseded arm's marker is dropped by the client's demux with its epoch; only the
        // fresh arm's marker reaches the merged stream.
        await harness.WriteAsync(response =>
        {
            BrokerProtocol.WriteCaughtUp(response, "C", harness.ArmEpochForDrive(firstArm, 'C'));
            BrokerProtocol.WriteCaughtUp(response, "C", harness.ArmEpochForDrive(secondArm, 'C'));
            BrokerProtocol.WriteJournalBatch(response, "C", harness.ArmEpochForDrive(secondArm, 'C'),
                new UsnJournalCursor(7, 510), [JournalEntryFactory.Create(2, 505, "fresh.txt")]);
        });

        await harness.AcknowledgeEndWatchAsync();

        var items = await consumption;
        Assert.AreEqual(2, items.Count);
        Assert.IsInstanceOfType<DriveCaughtUp>(items[0]);
        Assert.IsInstanceOfType<JournalBatch>(items[1]);
        var batch = (JournalBatch)items[1];
        Assert.AreEqual(510L, batch.NextUsn);
    }

    // Collecting then breaking is what writes the EndWatch the test reads back: disposing the
    // enumerator runs the source's finally, which stops the live watch on the borrowed client.
    static Task<List<WatchStreamItem>> ReadItemsAndBreakAsync(
        IAsyncEnumerable<WatchStreamItem> stream, int count)
    {
        return Task.Run(async () =>
        {
            var items = new List<WatchStreamItem>();
            await foreach (var item in stream)
            {
                items.Add(item);
                if (items.Count == count)
                {
                    break;
                }
            }

            return items;
        });
    }
}
