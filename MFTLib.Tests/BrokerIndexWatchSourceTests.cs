using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class BrokerIndexWatchSourceTests
{
    [TestMethod]
    public async Task WatchSource_StartsOneWatchForEveryTargetAndTagsEachBatch()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var consumption = ReadTwoItemsAndBreakAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100), new IndexWatchTarget('D', 9, 200)],
            harness.CancellationToken));

        var start = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, start.Kind);
        Assert.IsNotNull(start.DrivesSpec);
        Assert.IsTrue(start.DrivesSpec.StartsWith("C:7:100:", StringComparison.Ordinal));

        var entryC = JournalEntryFactory.Create(1, 105, "c.txt");
        var entryD = JournalEntryFactory.Create(2, 205, "d.txt");
        await harness.WriteAsync(response =>
        {
            BrokerProtocol.WriteJournalBatch(response, "C", harness.ArmEpochForDrive(start, 'C'),
                new UsnJournalCursor(7, 110), [entryC]);
            BrokerProtocol.WriteJournalBatch(response, "D", harness.ArmEpochForDrive(start, 'D'),
                new UsnJournalCursor(9, 210), [entryD]);
        });

        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);

        var items = await consumption;
        Assert.AreEqual(2, items.Count);
        var batchesByDrive = items.OfType<JournalBatch>().ToDictionary(batch => batch.DriveLetter);
        Assert.AreEqual(7ul, batchesByDrive['C'].JournalId);
        Assert.AreEqual(110L, batchesByDrive['C'].NextUsn);
        CollectionAssert.AreEqual(new[] { entryC }, batchesByDrive['C'].Entries.ToArray());
        Assert.AreEqual(9ul, batchesByDrive['D'].JournalId);
        Assert.AreEqual(210L, batchesByDrive['D'].NextUsn);
        CollectionAssert.AreEqual(new[] { entryD }, batchesByDrive['D'].Entries.ToArray());
    }

    [TestMethod]
    public async Task WatchSource_LeavesTheBorrowedClientReadyForAnotherWatch()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var consumption = ReadTwoItemsAndBreakAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100), new IndexWatchTarget('D', 9, 200)],
            harness.CancellationToken));

        var start = await harness.ReadFrameAsync();
        await harness.WriteAsync(response =>
        {
            BrokerProtocol.WriteJournalBatch(response, "C", harness.ArmEpochForDrive(start, 'C'),
                new UsnJournalCursor(7, 110), [JournalEntryFactory.Create(1, 105, "c.txt")]);
            BrokerProtocol.WriteJournalBatch(response, "D", harness.ArmEpochForDrive(start, 'D'),
                new UsnJournalCursor(9, 210), [JournalEntryFactory.Create(2, 205, "d.txt")]);
        });
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);
        await consumption;

        // The source's finally leaves the borrowed client usable rather than half torn down.
        await harness.Client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor>
        {
            ["C"] = new(7, 110)
        }, harness.CancellationToken);
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await harness.ReadFrameAsync()).Kind);
        var stop = harness.Client.StopLiveWatchAsync();
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);
        await stop;
    }

    [TestMethod]
    public async Task WatchSource_CompletesWhenTheTokenIsCancelled()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        using var cancellation = new CancellationTokenSource();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var consumption = ConsumeAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100)], cancellation.Token));

        Assert.AreEqual(BrokerFrameKind.StartWatch, (await harness.ReadFrameAsync()).Kind);
        await cancellation.CancelAsync();
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => consumption);
    }

    [TestMethod]
    public async Task WatchSource_CancellationLeavesBorrowedClientReadyForRestart()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        using var cancellation = new CancellationTokenSource();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var cancelledConsumption = ConsumeAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100)], cancellation.Token));

        Assert.AreEqual(BrokerFrameKind.StartWatch, (await harness.ReadFrameAsync()).Kind);
        await cancellation.CancelAsync();
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => cancelledConsumption);

        var restartedConsumption = ReadOneItemAndBreakAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100)], harness.CancellationToken));
        var startWatch = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, startWatch.Kind);
        var entry = JournalEntryFactory.Create(1, 105, "after-restart.txt");
        await harness.WriteAsync(response => BrokerProtocol.WriteJournalBatch(response, "C",
            harness.ArmEpochForDrive(startWatch, 'C'), new UsnJournalCursor(7, 110), [entry]));
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);

        var batch = (JournalBatch)(await restartedConsumption).Single();
        Assert.AreEqual('C', batch.DriveLetter);
        Assert.AreEqual(7ul, batch.JournalId);
        Assert.AreEqual(110L, batch.NextUsn);
        CollectionAssert.AreEqual(new[] { entry }, batch.Entries.ToArray());
    }

    [TestMethod]
    public async Task WatchSource_DoesNotFaultTheStreamForOneDrivesError()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var consumption = ReadTwoItemsAndBreakAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100), new IndexWatchTarget('D', 9, 200)],
            harness.CancellationToken));

        var start = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, start.Kind);
        await harness.WriteAsync(response =>
        {
            BrokerProtocol.WriteError(response, "D", harness.ArmEpochForDrive(start, 'D'), "journal wrapped");
            BrokerProtocol.WriteJournalBatch(response, "C", harness.ArmEpochForDrive(start, 'C'),
                new UsnJournalCursor(7, 110), [JournalEntryFactory.Create(1, 105, "c.txt")]);
        });
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);

        var items = await consumption;
        Assert.AreEqual('D', items.OfType<DriveWatchFailure>().Single().DriveLetter);
        Assert.AreEqual(110L, items.OfType<JournalBatch>().Single().NextUsn);
    }

    [TestMethod]
    public async Task WatchSource_CompletesNormallyWhenAllDriveSourcesComplete()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var consumption = ConsumeAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100), new IndexWatchTarget('D', 9, 200)],
            harness.CancellationToken));

        Assert.AreEqual(BrokerFrameKind.StartWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);

        await consumption;
    }

    [TestMethod]
    public async Task WatchSource_DoesNotConnectWhenTokenIsAlreadyCancelled()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => ConsumeAsync(
            source.StartWatching([new IndexWatchTarget('C', 7, 100)], cancellation.Token)));
        Assert.AreEqual(0, harness.ConnectionCount);
    }

    [TestMethod]
    public async Task WatchSource_RejectsNullTargetsBeforeConnecting()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            ConsumeAsync(source.StartWatching(null!, harness.CancellationToken)));
        Assert.AreEqual(0, harness.ConnectionCount);
    }

    [TestMethod]
    public async Task WatchSource_EmptyTargetsCompletesNormally()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var consumption = ConsumeAsync(source.StartWatching([], harness.CancellationToken));

        var start = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, start.Kind);
        Assert.AreEqual(string.Empty, start.DrivesSpec);
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);

        await consumption;
    }

    [TestMethod]
    public async Task StartWatching_WhileAStreamIsAlreadyRunning_ThrowsInvalidOperationException()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        using var cancellation = new CancellationTokenSource();
        var source = new BrokerIndexWatchSource(harness.ConnectAsync);

        // The synchronous prefix of StartWatching claims the stream before it ever awaits
        // anything, so the claim is already in place by the time this call returns here.
        var firstConsumption = ConsumeAsync(source.StartWatching(
            [new IndexWatchTarget('C', 7, 100)], cancellation.Token));
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await harness.ReadFrameAsync()).Kind);

        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            ConsumeAsync(source.StartWatching([new IndexWatchTarget('D', 9, 200)], harness.CancellationToken)));
        StringAssert.Contains(thrown.Message, "already running a stream");

        await cancellation.CancelAsync();
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => firstConsumption);
    }

    static async Task<List<WatchStreamItem>> ReadTwoItemsAndBreakAsync(IAsyncEnumerable<WatchStreamItem> source)
    {
        var items = new List<WatchStreamItem>();
        await foreach (var item in source)
        {
            items.Add(item);
            if (items.Count == 2)
            {
                break;
            }
        }

        return items;
    }

    static async Task<List<WatchStreamItem>> ReadOneItemAndBreakAsync(IAsyncEnumerable<WatchStreamItem> source)
    {
        var items = new List<WatchStreamItem>();
        await foreach (var item in source)
        {
            items.Add(item);
            break;
        }

        return items;
    }

    static async Task ConsumeAsync(IAsyncEnumerable<WatchStreamItem> source)
    {
        await foreach (var _ in source)
        {
        }
    }
}
