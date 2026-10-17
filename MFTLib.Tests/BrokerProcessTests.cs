using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The scan tests install JournalCheckpointCheck's process-wide journal override.
[TestClass]
[DoNotParallelize]
public partial class BrokerProcessTests
{
    static readonly UsnJournalCursor Armed = new(7, 1000);
    static readonly NtfsVolumeInformation Volume = new(1024 * 1000, 1024);
    [TestMethod]
    public async Task QueryVolume_ReturnsVolumeInformation()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryVolumeInformation = drive =>
            {
                Assert.AreEqual(new DriveKey('D'), drive);
                return Volume;
            }
        }));

        var volume = await broker.Process.QueryVolumeAsync('d', CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(Volume.MftValidDataLength, volume.MftValidDataLength);
        Assert.AreEqual(Volume.BytesPerFileRecordSegment, volume.BytesPerFileRecordSegment);
        Assert.AreEqual(Volume.MftRecordCount, volume.MftRecordCount);
    }

    [TestMethod]
    public async Task GrowUsnJournal_ReturnsSettings()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            GrowUsnJournal = (drive, maximumSize, allocationDelta) =>
            {
                Assert.AreEqual(new DriveKey('E'), drive);
                return new UsnJournalSettings { MaximumSize = maximumSize * 2, AllocationDelta = allocationDelta };
            }
        }));

        var settings = await broker.Process.GrowUsnJournalAsync('E', new UsnJournalSettings { MaximumSize = 65536, AllocationDelta = 4096 }, CancellationToken.None)
            .WaitAsync(HangGuard);

        Assert.AreEqual(131072L, settings.MaximumSize);
        Assert.AreEqual(4096L, settings.AllocationDelta);
    }

    [TestMethod]
    public async Task QueryVolume_HostError_ThrowsInvalidOperationWithMessage()
    {
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryVolumeInformation = _ => throw new UnauthorizedAccessException("access denied")
        }));

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            broker.Process.QueryVolumeAsync('C', CancellationToken.None).WaitAsync(HangGuard));

        Assert.AreEqual("access denied", exception.Message);
    }

    [TestMethod]
    public async Task ControlRequestIds_CancelledWaitDropsLateReply_NextRequestSucceeds()
    {
        var gate = new TestGate();
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryVolumeInformation = drive =>
            {
                if (drive != 'C')
                {
                    return Volume with { MftValidDataLength = 2048 * 1000 };
                }

                gate.MarkEntered();
                gate.WaitForRelease();
                return Volume;
            }
        }));
        using var cancellation = new CancellationTokenSource();

        var first = broker.Process.QueryVolumeAsync('C', cancellation.Token);
        await gate.Entered.WaitAsync(HangGuard);
        await cancellation.CancelAsync();
        await AssertCancelledAsync(first);
        gate.Release();
        var second = await broker.Process.QueryVolumeAsync('D', CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(2048L * 1000, second.MftValidDataLength, "The late reply to C must not answer D.");
        Assert.IsFalse(broker.Process.Ended.IsCompleted);
    }

    [TestMethod]
    public async Task HarnessHoldWrites_HoldsHostControlReplyUntilReleased()
    {
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(), new BrokerTestHarnessOptions
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
        Assert.AreEqual(2, broker.Process.Hooks.PendingRequestCount, "A cancelled wait keeps its id until its reply.");

        await broker.CloseControlAsync();
        var lost = await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => first.WaitAsync(HangGuard));

        Assert.IsNull(lost.Drive);
        Assert.AreEqual(0, broker.Process.Hooks.PendingRequestCount);
    }

    [TestMethod]
    public async Task ControlWrite_CancelledMidFrame_FinishesFrame_NextRequestSucceeds()
    {
        var control = new SplitFrameWrite(splitWriteNumber: 1);
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new() { QueryVolumeInformation = _ => Volume }),
            wrapClientStream: (name, stream) => name == "control" ? control.Wrap(stream) : stream);
        using var cancellation = new CancellationTokenSource();

        var held = broker.Process.QueryVolumeAsync('C', cancellation.Token);
        await control.Gate.Entered.WaitAsync(HangGuard);
        await cancellation.CancelAsync();
        control.Gate.Release();
        await AssertCancelledAsync(held);
        var next = await broker.Process.QueryVolumeAsync('D', CancellationToken.None).WaitAsync(HangGuard);

        Assert.AreEqual(Volume.MftRecordCount, next.MftRecordCount);
        Assert.IsFalse(broker.Process.Ended.IsCompleted, "A frame finished after its caller cancelled leaves the pipe usable.");
    }

    [TestMethod]
    public async Task ControlWrite_FailsMidFrame_EndsProcessLoudly()
    {
        var gate = new TestGate();
        var control = new SplitFrameWrite(splitWriteNumber: 2, failAfterPrefix: true);
        await using var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            QueryVolumeInformation = _ =>
            {
                gate.MarkEntered();
                gate.WaitForRelease();
                return Volume;
            }
        }),
            wrapClientStream: (name, stream) => name == "control" ? control.Wrap(stream) : stream);
        try
        {
            var pending = broker.Process.QueryVolumeAsync('C', CancellationToken.None);
            await gate.Entered.WaitAsync(HangGuard);
            var failing = broker.Process.QueryVolumeAsync('D', CancellationToken.None);

            await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => failing.WaitAsync(HangGuard));
            await Assert.ThrowsExceptionAsync<BrokerChannelLostException>(() => pending.WaitAsync(HangGuard));
            Assert.IsTrue(control.Gate.Entered.IsCompleted);
            await broker.Process.Ended.WaitAsync(HangGuard);
            StringAssert.Contains(await broker.Process.Ended.WaitAsync(HangGuard), "could not be written");
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
    public async Task Dispose_EndsHost()
    {
        var watchCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watching = new TestGate();
        var broker = new InProcessBroker(BrokerHostFactory.Create(new()
        {
            WatchDrive = (_, _, _, cancellationToken) =>
                WatchUntilCancelledAsync(watching, watchCancelled, cancellationToken)
        }));
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
        await broker.Process.Ended.WaitAsync(HangGuard);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task LaunchAsync_LaunchDeclined_Throws()
    {
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            BrokerProcess.LaunchAsync(_ => false, CancellationToken.None));

        StringAssert.Contains(exception.Message, "declined");
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
}
