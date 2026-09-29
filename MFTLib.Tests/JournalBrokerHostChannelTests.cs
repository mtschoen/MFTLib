using System.IO.Pipes;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The class installs JournalCheckpointCheck's process-wide journal override in its catch-up tests.
[TestClass]
[DoNotParallelize]
public partial class JournalBrokerHostChannelTests
{
    static readonly UsnJournalCursor Armed = new(7, 1000);
    static readonly NtfsVolumeInformation Volume = new(1024 * 1000, 1024, 512, 4096, 100, 10);

    [TestMethod]
    public async Task QueryVolume_RepliesWithRequestId()
    {
        var host = CreateHost(queryVolumeInfo: drive =>
        {
            Assert.AreEqual("C", drive);
            return Volume;
        });
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 41, @"c:\"));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.VolumeInfo, reply.Kind);
        Assert.AreEqual(41u, reply.RequestId);
        Assert.AreEqual(Volume.MftRecordCount, reply.RecordCount);
        Assert.AreEqual(Volume.BytesPerFileRecordSegment, reply.BytesPerFileRecordSegment);
        Assert.AreEqual(Volume.MftValidDataLength, reply.MftValidDataLength);
        Assert.IsNull(reply.Drive);
    }

    [TestMethod]
    public async Task GrowUsnJournal_RepliesWithRequestId()
    {
        var host = CreateHost(growUsnJournal: (drive, maximumSize, allocationDelta) =>
        {
            Assert.AreEqual("D", drive);
            return new UsnJournalSettings { MaximumSize = maximumSize, AllocationDelta = allocationDelta };
        });
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteGrowUsnJournal(writer, 9, "D", 65536, 4096));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.UsnJournalSettings, reply.Kind);
        Assert.AreEqual(9u, reply.RequestId);
        Assert.AreEqual(65536L, reply.JournalMaximumSize);
        Assert.AreEqual(4096L, reply.JournalAllocationDelta);
    }

    [TestMethod]
    public async Task QueryVolume_SourceThrows_RepliesErrorWithRequestId()
    {
        var host = CreateHost(queryVolumeInfo: _ => throw new UnauthorizedAccessException("access denied"));
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 3, "C"));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, reply.Kind);
        Assert.AreEqual(3u, reply.RequestId);
        Assert.AreEqual("access denied", reply.Message);
    }

    [TestMethod]
    public async Task ControlRequests_RunConcurrently()
    {
        var gate = new TestGate();
        var host = CreateHost(queryVolumeInfo: drive =>
        {
            if (drive == "C")
            {
                gate.MarkEntered();
                gate.WaitForReleaseAsync(CancellationToken.None).Wait(HostChannelHarness.HangGuard);
            }

            return Volume;
        });
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 1, "C"));
        await gate.Entered.WaitAsync(HostChannelHarness.HangGuard);
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 2, "D"));

        var first = await harness.ReadControlAsync();
        gate.Release();
        var second = await harness.ReadControlAsync();

        Assert.AreEqual(2u, first.RequestId, "The ungated query must be answered while the first one waits.");
        Assert.AreEqual(1u, second.RequestId);
    }

    [TestMethod]
    public async Task OpenChannel_ConnectsNamedPipeAndReplies()
    {
        var pipeName = "mftlib-channel-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var host = CreateHost(queryCursor: _ => Armed, watchDrive: IdleWatch);
        await using var harness = new HostChannelHarness(host,
            connectChannel: DefaultElevatedEntryRunner.ConnectDrivePipeAsync);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, 5, "C", pipeName));
        await server.WaitForConnectionAsync().WaitAsync(HostChannelHarness.HangGuard);
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.ChannelOpened, reply.Kind);
        Assert.AreEqual(5u, reply.RequestId);

        // The connected pipe is served: a watch armed at the tip reports CaughtUp over it.
        await HostChannelHarness.WriteFrameAsync(server, writer => BrokerProtocol.WriteStartWatch(writer, Armed));
        var caughtUp = await HostChannelHarness.ReadFrameAsync(server);
        Assert.AreEqual(BrokerFrameKind.CaughtUp, caughtUp?.Kind);
    }

    [TestMethod]
    public async Task OpenChannel_ConnectorNeverConnects_RepliesErrorAfterTimeout()
    {
        var clock = new FakeTimeProvider();
        var connection = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectToken = new TaskCompletionSource<CancellationToken>();
        var host = CreateHost(timeProvider: clock);
        await using var harness = new HostChannelHarness(host, connectChannel: (_, token) =>
        {
            connectToken.TrySetResult(token);
            return connection.Task;
        });

        await harness.SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, 12, "C", "never"));
        var reply = harness.ReadControlAsync();
        await AdvanceUntilCompleteAsync(clock, reply, JournalBrokerHost.ChannelConnectTimeout);

        Assert.AreEqual(BrokerFrameKind.Error, (await reply).Kind);
        Assert.AreEqual(12u, (await reply).RequestId);
        Assert.IsTrue((await connectToken.Task).IsCancellationRequested,
            "The connector's token must be cancelled once the host stops waiting.");

        // A connection that arrives after the host gave up is disposed rather than served.
        var late = new InMemoryPipePair();
        await using var lateLifetime = late;
        connection.SetResult(late.Host);
        Assert.IsNull(await HostChannelHarness.ReadFrameAsync(late.Client), "The late pipe must be closed.");
    }

    [TestMethod]
    public async Task OpenChannel_NoFirstRequest_ChannelEndsAfterTimeout()
    {
        var clock = new FakeTimeProvider();
        var host = CreateHost(timeProvider: clock);
        await using var harness = new HostChannelHarness(host);
        var pipe = await harness.OpenChannelAsync('C');

        var frame = HostChannelHarness.ReadFrameAsync(pipe);
        await AdvanceUntilCompleteAsync(clock, frame, JournalBrokerHost.FirstRequestTimeout);

        Assert.AreEqual(BrokerFrameKind.Error, (await frame)?.Kind);
        Assert.AreEqual(0u, (await frame)?.RequestId);
        Assert.IsNull(await HostChannelHarness.ReadFrameAsync(pipe), "The host must close the channel.");
    }

    [TestMethod]
    public async Task OpenChannel_ConnectorThrows_RepliesErrorWithRequestId()
    {
        var host = CreateHost();
        await using var harness = new HostChannelHarness(host,
            connectChannel: (_, _) => throw new IOException("pipe not found"));

        await harness.SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, 77, "C", "missing"));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, reply.Kind);
        Assert.AreEqual(77u, reply.RequestId);
        StringAssert.Contains(reply.Message, "pipe not found");
    }

    [TestMethod]
    public async Task DriveChannel_UnknownFirstFrame_WritesErrorAndCloses()
    {
        var host = CreateHost();
        await using var harness = new HostChannelHarness(host);
        var pipe = await harness.OpenChannelAsync('C');

        await HostChannelHarness.WriteFrameAsync(pipe, BrokerProtocol.WriteCaughtUp);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        StringAssert.Contains(frames[0].Message, "CaughtUp");
    }

    static JournalBrokerHost CreateHost(
        UsnJournalCursorQuery? queryCursor = null,
        MftRecordBatchSource? scanDrive = null,
        UsnJournalCatchUpSource? readJournal = null,
        JournalBatchSource? watchDrive = null,
        NtfsVolumeInformationQuery? queryVolumeInfo = null,
        GrowUsnJournalQuery? growUsnJournal = null,
        int processorCount = 4,
        TimeProvider? timeProvider = null)
    {
        return new JournalBrokerHost(
            queryCursor ?? (_ => Armed),
            scanDrive ?? ((_, _, _, _, _) => [[Record(5, ".", 3)]]),
            readJournal ?? ((_, since) => (Array.Empty<UsnJournalEntry>(), since)),
            watchDrive,
            queryVolumeInfo,
            growUsnJournal,
            processorCount,
            timeProvider);
    }

    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> IdleWatch(
        string drive, UsnJournalCursor since, IBrokerOperationReporter operation,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        operation.WaitingOnVolume();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    // Advances the fake clock by one timeout at a time until the task completes. A timer the host
    // creates after an advance fires on the next one, so the loop never depends on when the host
    // reached its wait.
    static async Task AdvanceUntilCompleteAsync(FakeTimeProvider clock, Task task, TimeSpan step)
    {
        using var guard = new CancellationTokenSource(HostChannelHarness.HangGuard);
        while (!task.IsCompleted)
        {
            clock.Advance(step);
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(20), guard.Token));
            guard.Token.ThrowIfCancellationRequested();
        }

        await task;
    }

    static MftRecord Record(ulong recordNumber, string name, ushort flags = 1)
    {
        return new MftRecord(recordNumber, 5, new MftRecordFields(flags), name, null);
    }
}
