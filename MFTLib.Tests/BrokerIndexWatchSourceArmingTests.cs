using System.Reflection;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

/// <summary>
///     Arming and disarming one drive on a stream that is already running. Every reply frame
///     carries the arm epoch read off the <c>StartWatch</c> frame the client just issued.
/// </summary>
[TestClass]
public partial class BrokerIndexWatchSourceArmingTests
{
    [TestMethod]
    public async Task ArmDriveAsync_OnALiveStream_AddsThatDrivesBatchesWithoutRestartingTheOthers()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var enumerator = source.StartWatching([new IndexWatchTarget('C', 7, 100)], harness.CancellationToken)
            .GetAsyncEnumerator(harness.CancellationToken);

        var firstItem = enumerator.MoveNextAsync();
        var firstStart = await harness.ReadFrameAsync();
        await source.ArmDriveAsync(new IndexWatchTarget('D', 9, 200), harness.CancellationToken);
        var secondStart = await harness.ReadFrameAsync();

        await harness.WriteAsync(response =>
        {
            BrokerProtocol.WriteJournalBatch(response, "C", harness.ArmEpochForDrive(firstStart, 'C'),
                new UsnJournalCursor(7, 110), [JournalEntryFactory.Create(1, 105, "c.txt")]);
            BrokerProtocol.WriteJournalBatch(response, "D", harness.ArmEpochForDrive(secondStart, 'D'),
                new UsnJournalCursor(9, 210), [JournalEntryFactory.Create(2, 205, "d.txt")]);
        });

        var items = new List<WatchStreamItem> { (await ExpectItemAsync(firstItem, enumerator)) };
        items.Add(await ExpectItemAsync(enumerator.MoveNextAsync(), enumerator));
        await FinishStreamAsync(harness, enumerator);

        var batchesByDrive = items.OfType<JournalBatch>().ToDictionary(batch => batch.DriveLetter);
        Assert.AreEqual(110L, batchesByDrive['C'].NextUsn);
        Assert.AreEqual(210L, batchesByDrive['D'].NextUsn);
    }

    [TestMethod]
    public async Task ArmDriveAsync_OnADriveWhoseReaderIsStillLive_ReplacesItWithoutHanging()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var enumerator = source.StartWatching([new IndexWatchTarget('C', 7, 100)], harness.CancellationToken)
            .GetAsyncEnumerator(harness.CancellationToken);

        var firstItem = enumerator.MoveNextAsync();
        var firstStart = await harness.ReadFrameAsync();
        await harness.WriteAsync(response => BrokerProtocol.WriteJournalBatch(response, "C",
            harness.ArmEpochForDrive(firstStart, 'C'), new UsnJournalCursor(7, 110),
            [JournalEntryFactory.Create(1, 105, "c.txt")]));
        Assert.AreEqual(110L, ((JournalBatch)await ExpectItemAsync(firstItem, enumerator)).NextUsn);

        // The reader is inside its await-foreach, not finished. Awaiting it before calling the
        // client would await a task nothing has told to finish; the harness token turns that hang
        // into a failed assertion rather than a wedged run.
        await source.ArmDriveAsync(new IndexWatchTarget('C', 7, 500), harness.CancellationToken);

        var secondStart = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, secondStart.Kind);
        StringAssert.StartsWith(secondStart.DrivesSpec, "C:7:500:");
        Assert.IsTrue(harness.ArmEpochForDrive(secondStart, 'C') > harness.ArmEpochForDrive(firstStart, 'C'));

        await harness.WriteAsync(response => BrokerProtocol.WriteJournalBatch(response, "C",
            harness.ArmEpochForDrive(secondStart, 'C'), new UsnJournalCursor(7, 520),
            [JournalEntryFactory.Create(2, 510, "after.txt")]));
        Assert.AreEqual(520L, ((JournalBatch)await ExpectItemAsync(enumerator.MoveNextAsync(), enumerator)).NextUsn);
        await FinishStreamAsync(harness, enumerator);
    }

    [TestMethod]
    public async Task ArmDriveAsync_OnADriveWhoseReaderIsStillLive_LeavesTheOtherDriveStreaming()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var enumerator = source.StartWatching(
                [new IndexWatchTarget('C', 7, 100), new IndexWatchTarget('D', 9, 200)],
                harness.CancellationToken)
            .GetAsyncEnumerator(harness.CancellationToken);

        var firstItem = enumerator.MoveNextAsync();
        var firstStart = await harness.ReadFrameAsync();
        await harness.WriteAsync(response => BrokerProtocol.WriteJournalBatch(response, "C",
            harness.ArmEpochForDrive(firstStart, 'C'), new UsnJournalCursor(7, 110),
            [JournalEntryFactory.Create(1, 105, "c.txt")]));
        await ExpectItemAsync(firstItem, enumerator);

        await source.ArmDriveAsync(new IndexWatchTarget('C', 7, 500), harness.CancellationToken);
        Assert.AreEqual(BrokerFrameKind.StartWatch, (await harness.ReadFrameAsync()).Kind);

        // Without the stop flag the replaced reader reads its normal channel completion as the
        // whole generation ending, completes the merged channel, and this never arrives.
        await harness.WriteAsync(response => BrokerProtocol.WriteJournalBatch(response, "D",
            harness.ArmEpochForDrive(firstStart, 'D'), new UsnJournalCursor(9, 210),
            [JournalEntryFactory.Create(2, 205, "d.txt")]));

        var item = (JournalBatch)await ExpectItemAsync(enumerator.MoveNextAsync(), enumerator);
        Assert.AreEqual('D', item.DriveLetter);
        Assert.AreEqual(210L, item.NextUsn);
        await FinishStreamAsync(harness, enumerator);
    }

    [TestMethod]
    public async Task DisarmDriveAsync_OnALiveStream_StopsThatDrivesItemsAndLeavesTheOtherFlowing()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var enumerator = source.StartWatching(
                [new IndexWatchTarget('C', 7, 100), new IndexWatchTarget('D', 9, 200)],
                harness.CancellationToken)
            .GetAsyncEnumerator(harness.CancellationToken);

        var firstItem = enumerator.MoveNextAsync();
        var start = await harness.ReadFrameAsync();
        await source.DisarmDriveAsync('C', harness.CancellationToken);
        Assert.AreEqual(BrokerFrameKind.DisarmDrive, (await harness.ReadFrameAsync()).Kind);

        // D's arrival is the ordering barrier proving C's frame was read and dropped.
        await harness.WriteAsync(response =>
        {
            BrokerProtocol.WriteJournalBatch(response, "C", harness.ArmEpochForDrive(start, 'C'),
                new UsnJournalCursor(7, 110), [JournalEntryFactory.Create(1, 105, "c.txt")]);
            BrokerProtocol.WriteJournalBatch(response, "D", harness.ArmEpochForDrive(start, 'D'),
                new UsnJournalCursor(9, 210), [JournalEntryFactory.Create(2, 205, "d.txt")]);
        });

        var item = (JournalBatch)await ExpectItemAsync(firstItem, enumerator);
        Assert.AreEqual('D', item.DriveLetter);
        await FinishStreamAsync(harness, enumerator);
    }

    [TestMethod]
    public async Task ArmDriveAsync_AfterADisarm_DropsAnItemTheOldReaderHadAlreadyQueuedAndYieldsTheOnesAfter()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var enumerator = source.StartWatching([new IndexWatchTarget('C', 7, 100)], harness.CancellationToken)
            .GetAsyncEnumerator(harness.CancellationToken);

        var firstItem = enumerator.MoveNextAsync();
        var firstStart = await harness.ReadFrameAsync();
        await harness.WriteAsync(response => BrokerProtocol.WriteJournalBatch(response, "C",
            harness.ArmEpochForDrive(firstStart, 'C'), new UsnJournalCursor(7, 110),
            [JournalEntryFactory.Create(1, 105, "first.txt")]));

        // Consuming the first batch parks the drain loop at its yield, so whatever the reader
        // forwards next stays on the merged channel instead of being drained straight through.
        Assert.AreEqual(110L, ((JournalBatch)await ExpectItemAsync(firstItem, enumerator)).NextUsn);

        // Drive D is armed on the client directly, outside the source, purely as an ordering
        // barrier: the client runs one demux loop over the pipe, so receiving D's batch proves the
        // frame written before it has already been routed into C's per-drive channel. Without that
        // proof this test would race, and the client's arm epoch would drop C's batch on the wire
        // rather than the source's arm generation dropping it on the merged channel.
        await harness.Client.SendStartWatchAsync(new Dictionary<string, UsnJournalCursor>
        {
            ["D"] = new(9, 200)
        }, harness.CancellationToken);
        var driveDStart = await harness.ReadFrameAsync();
        var driveDBatches = harness.Client.CreateBatchSource()("D", new UsnJournalCursor(9, 200),
            harness.CancellationToken).GetAsyncEnumerator(harness.CancellationToken);
        var driveDBarrier = driveDBatches.MoveNextAsync();

        await harness.WriteAsync(response =>
        {
            BrokerProtocol.WriteJournalBatch(response, "C", harness.ArmEpochForDrive(firstStart, 'C'),
                new UsnJournalCursor(7, 210), [JournalEntryFactory.Create(2, 205, "queued.txt")]);
            BrokerProtocol.WriteJournalBatch(response, "D", harness.ArmEpochForDrive(driveDStart, 'D'),
                new UsnJournalCursor(9, 210), [JournalEntryFactory.Create(3, 205, "barrier.txt")]);
        });
        Assert.IsTrue(await driveDBarrier, "the barrier batch never arrived, so the demux never ran");

        // Disarming completes C's channel, and its reader drains what is already queued there onto
        // the merged channel before it ends. The disarm awaits that reader, so once it returns the
        // 210 batch is provably on the merged channel under the superseded arm generation.
        await source.DisarmDriveAsync('C', harness.CancellationToken);
        Assert.AreEqual(BrokerFrameKind.DisarmDrive, (await harness.ReadFrameAsync()).Kind);
        await source.ArmDriveAsync(new IndexWatchTarget('C', 7, 500), harness.CancellationToken);
        var secondStart = await harness.ReadFrameAsync();

        await harness.WriteAsync(response => BrokerProtocol.WriteJournalBatch(response, "C",
            harness.ArmEpochForDrive(secondStart, 'C'), new UsnJournalCursor(7, 520),
            [JournalEntryFactory.Create(4, 510, "after.txt")]));

        // The dropped item was legitimately delivered under the epoch current when it arrived,
        // so only the source's own arm generation can drop it.
        var item = (JournalBatch)await ExpectItemAsync(enumerator.MoveNextAsync(), enumerator);
        Assert.AreEqual(520L, item.NextUsn);
        await driveDBatches.DisposeAsync();
        await FinishStreamAsync(harness, enumerator);
    }

    [TestMethod]
    public async Task ArmDriveAsync_AfterADisarm_DropsAnErrorFrameFromTheSupersededArm()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();
        var enumerator = source.StartWatching([new IndexWatchTarget('C', 7, 100)], harness.CancellationToken)
            .GetAsyncEnumerator(harness.CancellationToken);

        var firstItem = enumerator.MoveNextAsync();
        var firstStart = await harness.ReadFrameAsync();
        await source.DisarmDriveAsync('C', harness.CancellationToken);
        Assert.AreEqual(BrokerFrameKind.DisarmDrive, (await harness.ReadFrameAsync()).Kind);
        await source.ArmDriveAsync(new IndexWatchTarget('C', 7, 500), harness.CancellationToken);
        var secondStart = await harness.ReadFrameAsync();

        await harness.WriteAsync(response =>
        {
            BrokerProtocol.WriteError(response, "C", harness.ArmEpochForDrive(firstStart, 'C'), "stale wrap");
            BrokerProtocol.WriteJournalBatch(response, "C", harness.ArmEpochForDrive(secondStart, 'C'),
                new UsnJournalCursor(7, 520), [JournalEntryFactory.Create(2, 510, "after.txt")]);
        });

        var item = await ExpectItemAsync(firstItem, enumerator);
        Assert.IsInstanceOfType<JournalBatch>(item);
        Assert.AreEqual(520L, ((JournalBatch)item).NextUsn);
        await FinishStreamAsync(harness, enumerator);
    }

    [TestMethod]
    public async Task ArmDriveAsync_WithNoStreamRunning_ThrowsInvalidOperationException()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerMftBlockProducer(harness.ConnectAsync).CreateWatchSource();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => source.ArmDriveAsync(new IndexWatchTarget('C', 7, 100), harness.CancellationToken));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => source.DisarmDriveAsync('C', harness.CancellationToken));

        Assert.AreEqual(0, harness.ConnectionCount);
    }

    [TestMethod]
    public async Task DriveFaulting_WhileAnotherDriveIsBetweenDisarmAndReArm_KeepsTheStreamAlive()
    {
        await using var harness = new ScriptedWatchBrokerHarness();

        // Built directly rather than through the producer, whose interface hides the per-drive
        // reader task this test waits on below.
        var source = new BrokerIndexWatchSource(harness.ConnectAsync);
        var enumerator = source.StartWatching(
                [new IndexWatchTarget('C', 7, 100), new IndexWatchTarget('D', 9, 200)],
                harness.CancellationToken)
            .GetAsyncEnumerator(harness.CancellationToken);

        var firstItem = enumerator.MoveNextAsync();
        var start = await harness.ReadFrameAsync();

        // Where a rescan leaves the drive it is rebuilding for the whole duration of its scan:
        // disarmed, out of the reader map, and not yet re-armed.
        await source.DisarmDriveAsync('C', harness.CancellationToken);
        Assert.AreEqual(BrokerFrameKind.DisarmDrive, (await harness.ReadFrameAsync()).Kind);

        // Taken before D faults, because a faulting reader takes itself out of the map on its way
        // out. Its presence is also the proof that disarming C left D alone.
        var driveDReader = source.DriveReaderForTest('D');
        Assert.IsNotNull(driveDReader, "disarming C must leave D's reader armed.");

        // D fails inside that window. With C absent from the reader map, D's reader must not read
        // the map emptying as the last reader leaving and complete the merged channel, which would
        // end every drive's watch and leave the rescan nothing to re-arm onto.
        await harness.WriteAsync(response =>
            BrokerProtocol.WriteError(response, "D", harness.ArmEpochForDrive(start, 'D'), "D wrapped"));

        // This wait is the whole reason the test is deterministic. D's reader publishes its failure
        // item before it decides whether it was the last reader, so consuming that item leaves the
        // re-arm below racing the decision, and the race is winnable from either side. Once the
        // reader's task has ended the decision is behind us and the assertions describe the
        // marker's behaviour rather than the scheduler's.
        await driveDReader.WaitAsync(harness.CancellationToken);

        var failure = (DriveWatchFailure)await ExpectItemAsync(firstItem, enumerator);
        Assert.AreEqual('D', failure.DriveLetter);

        await source.ArmDriveAsync(new IndexWatchTarget('C', 7, 500), harness.CancellationToken);
        var secondStart = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, secondStart.Kind);
        await harness.WriteAsync(response => BrokerProtocol.WriteJournalBatch(response, "C",
            harness.ArmEpochForDrive(secondStart, 'C'), new UsnJournalCursor(7, 520),
            [JournalEntryFactory.Create(1, 510, "after.txt")]));

        var batch = (JournalBatch)await ExpectItemAsync(enumerator.MoveNextAsync(), enumerator);
        Assert.AreEqual('C', batch.DriveLetter);
        Assert.AreEqual(520L, batch.NextUsn);
        await FinishStreamAsync(harness, enumerator);
    }

    [TestMethod]
    public async Task ReaderFaulting_WhileItsOwnDisarmIsInFlight_DoesNotCompleteTheMergedStream()
    {
        await using var harness = new ScriptedWatchBrokerHarness();
        var source = new BrokerIndexWatchSource(harness.ConnectAsync);
        var enumerator = source.StartWatching([new IndexWatchTarget('C', 7, 100)], harness.CancellationToken)
            .GetAsyncEnumerator(harness.CancellationToken);

        var firstItem = enumerator.MoveNextAsync();
        var start = await harness.ReadFrameAsync();

        // Captured before the disarm below removes it from the source's reader map, so the
        // test can wait on the exact task whose catch block makes the decision under test.
        var readerC = source.DriveReaderForTest('C');
        Assert.IsNotNull(readerC, "the drive must have a live reader before it can be disarmed.");

        // Held for the width of the race: DisarmDriveAsync's own per-drive state (the stop
        // flag this test exercises) is set by its synchronous prefix before it ever reaches
        // this gate, but the client-side call that would otherwise retire the reader cleanly
        // cannot proceed past it, so the reader's channel stays armed at its original epoch
        // for as long as the gate is held.
        var armOrderingGate = GetPrivateField<SemaphoreSlim>(harness.Client, "_armOrderingGate");
        await armOrderingGate.WaitAsync(harness.CancellationToken);
        Task disarmTask;
        try
        {
            disarmTask = source.DisarmDriveAsync('C', harness.CancellationToken);

            // The channel is still armed at its original epoch, so this error reaches the
            // reader instead of being dropped, faulting it while the source already
            // considers this drive's stop requested.
            await harness.WriteAsync(response =>
                BrokerProtocol.WriteError(response, "C", harness.ArmEpochForDrive(start, 'C'), "C wrapped"));

            // Deterministic happens-before: once the reader task has ended, its decision
            // about completing the merged stream is already behind it.
            await readerC.WaitAsync(harness.CancellationToken);
        }
        finally
        {
            armOrderingGate.Release();
        }

        await disarmTask;
        Assert.AreEqual(BrokerFrameKind.DisarmDrive, (await harness.ReadFrameAsync()).Kind);

        // The failure the reader wrote carries the arm generation it was started with, which
        // the disarm above already superseded, so the merged stream's own staleness filter
        // drops it before it ever reaches firstItem (that drop is already covered by
        // ArmDriveAsync_AfterADisarm_DropsAnErrorFrameFromTheSupersededArm). What this test
        // checks is what the drop did not also do: if the fault had wrongly completed the
        // merged writer, nothing below would ever arrive, because the channel would already
        // be done before this re-arm's batch could land, and firstItem would still be the
        // pending call that observes it.
        await source.ArmDriveAsync(new IndexWatchTarget('C', 7, 500), harness.CancellationToken);
        var secondStart = await harness.ReadFrameAsync();
        Assert.AreEqual(BrokerFrameKind.StartWatch, secondStart.Kind);
        await harness.WriteAsync(response => BrokerProtocol.WriteJournalBatch(response, "C",
            harness.ArmEpochForDrive(secondStart, 'C'), new UsnJournalCursor(7, 520),
            [JournalEntryFactory.Create(1, 510, "after.txt")]));

        var batch = (JournalBatch)await ExpectItemAsync(firstItem, enumerator);
        Assert.AreEqual(520L, batch.NextUsn);
        await FinishStreamAsync(harness, enumerator);
    }

    static T GetPrivateField<T>(JournalBrokerClient client, string fieldName)
    {
        return (T)typeof(JournalBrokerClient)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;
    }

    static async Task<WatchStreamItem> ExpectItemAsync(ValueTask<bool> moveNext,
        IAsyncEnumerator<WatchStreamItem> enumerator)
    {
        Assert.IsTrue(await moveNext, "The stream ended before it yielded the expected item.");
        return enumerator.Current;
    }

    /// <summary>Disposing the enumerator ends the watch, which is the EndWatch handshake below.</summary>
    static async Task FinishStreamAsync(ScriptedWatchBrokerHarness harness,
        IAsyncEnumerator<WatchStreamItem> enumerator)
    {
        var disposal = enumerator.DisposeAsync();
        Assert.AreEqual(BrokerFrameKind.EndWatch, (await harness.ReadFrameAsync()).Kind);
        await harness.WriteAsync(BrokerProtocol.WriteEndWatchAck);
        await disposal;
    }

}
