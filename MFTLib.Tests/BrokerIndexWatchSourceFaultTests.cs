using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     The broker-side half of per-drive fault isolation, driven by scripted broker frames. Every
///     reply frame carries the arm epoch read off the <c>StartWatch</c> frame the client just
///     issued, never a literal, so these pin behaviour rather than the epoch issue order.
/// </summary>
[TestClass]
public class BrokerIndexWatchSourceFaultTests
{
    [TestMethod]
    public async Task WatchSource_YieldsAPerDriveFaultItemAndKeepsTheOtherDriveFlowing()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var consumption = ReadItemsAndBreakAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100), new IndexWatchTarget('D', 9, 200)],
            harness.CancellationToken), count: 2);

        var start = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, start.Kind);
        var entryC = JournalEntryFactory.Create(1, 105, "c.txt");
        await harness.WriteAsync(response =>
        {
            BrokerProtocol.WriteError(response, "D", harness.ArmEpochForDrive(start, 'D'), "journal wrapped");
            BrokerProtocol.WriteJournalBatch(response, "C", harness.ArmEpochForDrive(start, 'C'),
                new UsnJournalCursor(7, 110), [entryC]);
        });

        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);

        // Two reader tasks write into one channel, so assert by item type and drive, never by index.
        var items = await consumption;
        var failure = items.OfType<DriveWatchFailure>().Single();
        Assert.AreEqual('D', failure.DriveLetter);
        Assert.AreEqual("journal wrapped", failure.Exception.Message);
        var batch = items.OfType<JournalBatch>().Single();
        Assert.AreEqual('C', batch.DriveLetter);
        Assert.AreEqual(7ul, batch.JournalId);
        Assert.AreEqual(110L, batch.NextUsn);
    }

    [TestMethod]
    public async Task WatchSource_CompletesAfterEveryDriveHasFaulted()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var consumption = CollectAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100), new IndexWatchTarget('D', 9, 200)],
            harness.CancellationToken));

        var start = await harness.ReadFrameAsync();
        await harness.WriteAsync(response =>
        {
            BrokerProtocol.WriteError(response, "C", harness.ArmEpochForDrive(start, 'C'), "C wrapped");
            BrokerProtocol.WriteError(response, "D", harness.ArmEpochForDrive(start, 'D'), "D wrapped");
        });
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);

        var items = await consumption;
        Assert.AreEqual(2, items.Count);
        CollectionAssert.AreEquivalent(new[] { 'C', 'D' },
            items.OfType<DriveWatchFailure>().Select(failure => failure.DriveLetter).ToArray());
    }

    [TestMethod]
    public async Task WatchSource_FaultsTheWholeStreamWhenItCannotConnect()
    {
        var connectFailure = new IOException("the broker never launched");
        var source = new BrokerIndexWatchSource(_ => throw connectFailure);
        var items = new List<WatchStreamItem>();

        var thrown = await Assert.ThrowsExceptionAsync<IOException>(async () =>
        {
            await foreach (var item in source.StartWatching(
                [new IndexWatchTarget('C', 7, 100)], CancellationToken.None))
            {
                items.Add(item);
            }
        });

        Assert.AreSame(connectFailure, thrown);
        Assert.AreEqual(0, items.Count);
    }

    [TestMethod]
    public async Task WatchSource_RejectsTwoTargetsForOneDriveBeforeConnecting()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();

        var thrown = await Assert.ThrowsExceptionAsync<ArgumentException>(() => CollectAsync(
            source.StartWatching([new IndexWatchTarget('C', 7, 100), new IndexWatchTarget('C', 9, 200)],
                harness.CancellationToken)));

        Assert.AreEqual("targets", thrown.ParamName);
        StringAssert.Contains(thrown.Message, "C");
        Assert.AreEqual(0, harness.ConnectionCount);
    }

    [TestMethod]
    public async Task DisarmDriveAsync_ThatThrows_LeavesNoMarkerBlockingTheStreamFromCompleting()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var consumption = CollectAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100), new IndexWatchTarget('D', 9, 200)],
            harness.CancellationToken));

        var start = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, start.Kind);

        // A disarm claims the drive's per-drive state, including its marker as a drive awaiting a
        // reader, before it does anything that can fail. A drive letter the client cannot normalize
        // is the one trigger that reaches that failure deterministically; a broker write that
        // errors and a cancelled wait for the retiring reader throw from the same two awaits and
        // unwind through the same finally.
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => source.DisarmDriveAsync('1', harness.CancellationToken));

        // Both real drives now fault. A marker left behind by the failed disarm gates the whole
        // stream, not just its own drive, so nothing would ever complete the merged channel and
        // the frame read below would never see EndWatch.
        await harness.WriteAsync(response =>
        {
            BrokerProtocol.WriteError(response, "C", harness.ArmEpochForDrive(start, 'C'), "C wrapped");
            BrokerProtocol.WriteError(response, "D", harness.ArmEpochForDrive(start, 'D'), "D wrapped");
        });
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);

        var items = await consumption.WaitAsync(TimeSpan.FromSeconds(10));
        CollectionAssert.AreEquivalent(new[] { 'C', 'D' },
            items.OfType<DriveWatchFailure>().Select(failure => failure.DriveLetter).ToArray());
    }

    static async Task<List<WatchStreamItem>> ReadItemsAndBreakAsync(
        IAsyncEnumerable<WatchStreamItem> source, int count)
    {
        var items = new List<WatchStreamItem>();
        await foreach (var item in source)
        {
            items.Add(item);
            if (items.Count == count)
            {
                break;
            }
        }

        return items;
    }

    static async Task<List<WatchStreamItem>> CollectAsync(IAsyncEnumerable<WatchStreamItem> source)
    {
        var items = new List<WatchStreamItem>();
        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
    }
}
