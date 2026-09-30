using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The scan tests install JournalCheckpointCheck's process-wide journal override.
[TestClass]
[DoNotParallelize]
public partial class BrokerProcessTests
{
    static readonly UsnJournalCursor Armed = new(7, 1000);
    static readonly NtfsVolumeInformation Volume = new(1024 * 1000, 1024, 512, 4096, 100, 10);
    static readonly TimeSpan HangGuard = HostChannelHarness.HangGuard;

    [TestMethod]
    public async Task QueryVolume_ReturnsVolumeInformation()
    {
        await using var broker = new InProcessBroker(CreateHost(queryVolumeInfo: drive =>
        {
            Assert.AreEqual("D", drive);
            return Volume;
        }));

        var volume = await broker.Process.QueryVolumeAsync('d', CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(Volume.MftValidDataLength, volume.MftValidDataLength);
        Assert.AreEqual(Volume.BytesPerFileRecordSegment, volume.BytesPerFileRecordSegment);
        Assert.AreEqual(Volume.MftRecordCount, volume.MftRecordCount);
    }

    [TestMethod]
    public async Task GrowUsnJournal_ReturnsSettings()
    {
        await using var broker = new InProcessBroker(CreateHost(growUsnJournal: (drive, maximumSize, allocationDelta) =>
        {
            Assert.AreEqual("E", drive);
            return new UsnJournalSettings { MaximumSize = maximumSize * 2, AllocationDelta = allocationDelta };
        }));

        var settings = await broker.Process.GrowUsnJournalAsync('E', 65536, 4096, CancellationToken.None)
            .WaitAsync(HangGuard);

        Assert.AreEqual(131072L, settings.MaximumSize);
        Assert.AreEqual(4096L, settings.AllocationDelta);
    }

    [TestMethod]
    public async Task QueryVolume_HostError_ThrowsInvalidOperationWithMessage()
    {
        await using var broker = new InProcessBroker(CreateHost(
            queryVolumeInfo: _ => throw new UnauthorizedAccessException("access denied")));

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            broker.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual("access denied", exception.Message);
    }

    [TestMethod]
    public async Task ControlRequestIds_CancelledWaitDropsLateReply_NextRequestSucceeds()
    {
        var gate = new TestGate();
        await using var broker = new InProcessBroker(CreateHost(queryVolumeInfo: drive =>
        {
            if (drive != "C")
            {
                return Volume with { MftValidDataLength = 2048 * 1000 };
            }

            gate.MarkEntered();
            gate.WaitForRelease();
            return Volume;
        }));
        using var cancellation = new CancellationTokenSource();

        var first = broker.Process.QueryVolumeAsync('C', cancellation.Token);
        await gate.Entered.WaitAsync(HangGuard);
        await cancellation.CancelAsync();
        await AssertCancelledAsync(first);
        gate.Release();
        var second = await broker.Process.QueryVolumeAsync('D', CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(2048L * 1000, second.MftValidDataLength, "The late reply to C must not answer D.");
        Assert.IsFalse(broker.Process.HasEnded);
    }

    [TestMethod]
    public async Task HarnessHoldWrites_HoldsHostControlReplyUntilReleased()
    {
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(CreateHost(), new MFTLibTestExtensions.BrokerTestHarnessOptions
        {
            HoldWrites = pipeName =>
            {
                if (pipeName != "control")
                {
                    return Task.CompletedTask;
                }

                attempted.TrySetResult();
                return release.Task;
            }
        });

        var query = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        await attempted.Task.WaitAsync(HangGuard);
        Assert.IsFalse(query.IsCompleted, "The host's reply is held.");
        release.SetResult();

        Assert.AreEqual(Volume.MftRecordCount, (await query.WaitAsync(HangGuard)).MftRecordCount);
    }

    [TestMethod]
    public async Task RequestIds_WrapAround_SkipZeroAndOutstanding()
    {
        await using var broker = new ScriptedBroker();
        var pending = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        Assert.AreEqual(1u, (await broker.ReadRequestAsync()).RequestId);
        broker.Process.StartingRequestIdForTest = () => uint.MaxValue - 1;

        var atMaximum = broker.Process.QueryVolumeAsync('D', CancellationToken.None);
        var maximumRequest = await broker.ReadRequestAsync();
        var afterWrap = broker.Process.QueryVolumeAsync('E', CancellationToken.None);
        var wrappedRequest = await broker.ReadRequestAsync();

        Assert.AreEqual(uint.MaxValue, maximumRequest.RequestId);
        Assert.AreEqual(2u, wrappedRequest.RequestId, "Zero and the outstanding id 1 are skipped.");
        await broker.CloseControlAsync();
        foreach (var request in new[] { pending, atMaximum, afterWrap })
        {
            await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => request.WaitAsync(HangGuard));
        }
    }

    [TestMethod]
    public async Task RequestIds_SpaceExhausted_ThrowsInvalidOperationAndOtherRequestsUnaffected()
    {
        await using var broker = new ScriptedBroker();
        broker.Process.MaximumRequestIdForTest = 3;
        var requests = new List<Task<NtfsVolumeInformation>>();
        var ids = new List<uint>();
        for (var index = 0; index < 3; index++)
        {
            requests.Add(broker.Process.QueryVolumeAsync('C', CancellationToken.None));
            ids.Add((await broker.ReadRequestAsync()).RequestId);
        }

        var exhausted = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            broker.Process.QueryVolumeAsync('C', CancellationToken.None));
        Assert.AreEqual("No broker request id is free", exhausted.Message);
        CollectionAssert.AreEqual(new uint[] { 1, 2, 3 }, ids);

        await broker.WriteControlAsync(writer => BrokerProtocol.WriteVolumeInfo(writer, 2, 1000, 1024, 1024 * 1000));
        Assert.AreEqual(1000L, (await requests[1].WaitAsync(HangGuard)).MftRecordCount);
        var fifth = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        var fifthRequest = await broker.ReadRequestAsync();
        await broker.WriteControlAsync(writer =>
            BrokerProtocol.WriteVolumeInfo(writer, fifthRequest.RequestId, 500, 1024, 1024 * 500));

        Assert.AreEqual(2u, fifthRequest.RequestId);
        Assert.AreEqual(500L, (await fifth.WaitAsync(HangGuard)).MftRecordCount);
        Assert.IsFalse(requests[0].IsCompleted || requests[2].IsCompleted, "The other requests stay pending.");
        Assert.IsFalse(broker.Process.HasEnded);
    }

    [TestMethod]
    public async Task RequestIds_ReleasedOnProcessEnd()
    {
        await using var broker = new ScriptedBroker();
        var first = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        await broker.ReadRequestAsync();
        using var cancellation = new CancellationTokenSource();
        var abandoned = broker.Process.QueryVolumeAsync('D', cancellation.Token);
        await broker.ReadRequestAsync();
        await cancellation.CancelAsync();
        await AssertCancelledAsync(abandoned);
        Assert.AreEqual(2, broker.Process.PendingRequestCountForTest, "A cancelled wait keeps its id until its reply.");

        await broker.CloseControlAsync();
        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => first.WaitAsync(HangGuard));

        Assert.IsNull(lost.DriveLetter);
        Assert.AreEqual(0, broker.Process.PendingRequestCountForTest);
    }

    [TestMethod]
    public async Task ControlWrite_CancelledMidFrame_FinishesFrame_NextRequestSucceeds()
    {
        var control = new SplitFrameWrite(splitWriteNumber: 1);
        await using var broker = new InProcessBroker(CreateHost(queryVolumeInfo: _ => Volume),
            wrapClientStream: (name, stream) => name == "control" ? control.Wrap(stream) : stream);
        using var cancellation = new CancellationTokenSource();

        var held = broker.Process.QueryVolumeAsync('C', cancellation.Token);
        await control.Gate.Entered.WaitAsync(HangGuard);
        await cancellation.CancelAsync();
        control.Gate.Release();
        await AssertCancelledAsync(held);
        var next = await broker.Process.QueryVolumeAsync('D', CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(Volume.MftRecordCount, next.MftRecordCount);
        Assert.IsFalse(broker.Process.HasEnded, "A frame finished after its caller cancelled leaves the pipe usable.");
    }

    [TestMethod]
    public async Task ControlWrite_FailsMidFrame_EndsProcessLoudly()
    {
        var gate = new TestGate();
        var control = new SplitFrameWrite(splitWriteNumber: 2, failAfterPrefix: true);
        await using var broker = new InProcessBroker(CreateHost(queryVolumeInfo: _ =>
            {
                gate.MarkEntered();
                gate.WaitForRelease();
                return Volume;
            }),
            wrapClientStream: (name, stream) => name == "control" ? control.Wrap(stream) : stream);
        var ended = new List<string>();
        broker.Process.Ended += ended.Add;
        try
        {
            var pending = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
            await gate.Entered.WaitAsync(HangGuard);
            var failing = broker.Process.QueryVolumeAsync('D', CancellationToken.None);

            await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => failing.WaitAsync(HangGuard));
            await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => pending.WaitAsync(HangGuard));
            Assert.IsTrue(control.Gate.Entered.IsCompleted);
            Assert.IsTrue(broker.Process.HasEnded);
            Assert.AreEqual(1, ended.Count);
            StringAssert.Contains(ended[0], "could not be written");
        }
        finally
        {
            gate.Release();
        }

        // The host read the prefix of a frame whose rest never came, which fails its session; like a
        // broker process that exits, that reaches the client only as closed pipes, never as a throw.
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);
    }

    [TestMethod]
    public async Task HostEnds_EveryPendingRequestFailsWithChannelLost_EndedFiresOnce()
    {
        await using var broker = new ScriptedBroker();
        var ended = new List<string>();
        broker.Process.Ended += ended.Add;
        var first = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        var second = broker.Process.GrowUsnJournalAsync('D', 1, 1, CancellationToken.None);
        await broker.ReadRequestAsync();
        await broker.ReadRequestAsync();

        await broker.CloseControlAsync();

        foreach (var request in new Task[] { first, second })
        {
            var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => request.WaitAsync(HangGuard));
            Assert.IsNull(lost.DriveLetter);
        }

        Assert.IsTrue(broker.Process.HasEnded);
        await broker.Process.DisposeAsync().AsTask().WaitAsync(HangGuard);
        Assert.AreEqual(1, ended.Count, "Ended fires once, however the process then ends.");
        await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() =>
            broker.Process.QueryVolumeAsync('C', CancellationToken.None));
    }

    [TestMethod]
    public async Task Dispose_EndsHost()
    {
        var watchCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watching = new TestGate();
        var broker = new InProcessBroker(CreateHost(watchDrive: (_, _, _, cancellationToken) =>
            WatchUntilCancelledAsync(watching, watchCancelled, cancellationToken)));
        try
        {
            await using var channel = await broker.Process.OpenChannelAsync('C',
                    writer => BrokerProtocol.WriteStartWatch(writer, Armed), CancellationToken.None)
                .WaitAsync(HangGuard);
            await watching.Entered.WaitAsync(HangGuard);
        }
        finally
        {
            await broker.DisposeAsync();
        }

        Assert.IsTrue(watchCancelled.Task.IsCompleted, "Disposal returns only after the host ended its channels.");
        Assert.IsTrue(broker.Process.HasEnded);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task LaunchAsync_LaunchDeclined_Throws()
    {
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            BrokerProcess.LaunchAsync(_ => false, CancellationToken.None));

        StringAssert.Contains(exception.Message, "declined");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task LaunchAsync_NeverConnects_TimesOut()
    {
        var exception = await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            BrokerProcess.LaunchAsync(_ => true, TimeSpan.FromMilliseconds(50), CancellationToken.None));

        StringAssert.Contains(exception.Message, "Timed out waiting 50ms");
        StringAssert.Contains(exception.Message, "mftlib-broker-");
        StringAssert.Contains(exception.Message, "launched, but never connected");
    }

    // Whatever ends the control pipe's reader ends the process: a pending request fails with the
    // reason and Ended fires once.
    [DataTestMethod]
    [DataRow("unroutable frame", "CaughtUp")]
    [DataRow("truncated frame", "Truncated")]
    [DataRow("eof", "closed its control pipe")]
    public async Task ControlExchange_DemuxExit_CompletesPendingQuery(string ending, string expectedReason)
    {
        await using var broker = new ScriptedBroker();
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Process.Ended += reason => ended.TrySetResult(reason);
        var query = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
        Assert.AreEqual(BrokerFrameKind.QueryVolume, (await broker.ReadRequestAsync()).Kind);

        if (ending == "unroutable frame")
        {
            await broker.WriteControlAsync(BrokerProtocol.WriteCaughtUp);
        }
        else
        {
            if (ending == "truncated frame")
            {
                await broker.WriteControlAsync(writer =>
                {
                    byte[] bytes = [10, 0, 0, 0, 1, 2, 3];
                    bytes.CopyTo(writer.GetSpan(bytes.Length));
                    writer.Advance(bytes.Length);
                });
            }

            await broker.CloseControlAsync();
        }

        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => query.WaitAsync(HangGuard));
        Assert.IsNull(lost.DriveLetter);
        StringAssert.Contains(await ended.Task.WaitAsync(HangGuard), expectedReason);
        Assert.IsTrue(broker.Process.HasEnded);
    }

    // A cancelled wait may surface as OperationCanceledException or its TaskCanceledException subtype.
    static async Task AssertCancelledAsync(Task task)
    {
        try
        {
            await task.WaitAsync(HangGuard);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("Expected the task to be cancelled.");
    }

    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchUntilCancelledAsync(
        TestGate watching, TaskCompletionSource cancelled,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        watching.MarkEntered();
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            cancelled.TrySetResult();
        }

        yield break;
    }

    static JournalBrokerHost CreateHost(
        UsnJournalCursorQuery? queryCursor = null,
        MftRecordBatchSource? scanDrive = null,
        UsnJournalCatchUpSource? readJournal = null,
        JournalBatchSource? watchDrive = null,
        NtfsVolumeInformationQuery? queryVolumeInfo = null,
        GrowUsnJournalQuery? growUsnJournal = null,
        TimeProvider? timeProvider = null)
    {
        return new JournalBrokerHost(
            queryCursor ?? (_ => Armed),
            scanDrive ?? ((_, _, _, _, _) => [[Record(5, ".", 3)], [Record(20, "file.txt")]]),
            readJournal ?? ((_, since, _) => (Array.Empty<UsnJournalEntry>(), since)),
            watchDrive,
            queryVolumeInfo ?? (_ => Volume),
            growUsnJournal,
            processorCount: 4,
            timeProvider: timeProvider);
    }

    static MftRecord Record(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name, null);
    }
}
