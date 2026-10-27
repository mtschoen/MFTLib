using System.Text;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The class installs JournalCheckpointCheck's process-wide journal override in its catch-up tests.
[TestClass]
[DoNotParallelize]
public partial class JournalBrokerHostChannelTests
{
    static readonly UsnJournalCursor Armed = new(7, 1000);
    static readonly NtfsVolumeInformation Volume = new(1024 * 1000, 1024);

    [TestMethod]
    public async Task QueryVolume_RepliesWithRequestId()
    {
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = drive =>
            {
                Assert.AreEqual(new DriveKey('C'), drive);
                return Volume;
            }
        });
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 41, 'c'));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.VolumeInfo, reply.Kind);
        Assert.AreEqual(41u, reply.RequestId);
        Assert.AreEqual(Volume.BytesPerFileRecordSegment, reply.BytesPerFileRecordSegment);
        Assert.AreEqual(Volume.MftValidDataLength, reply.MftValidDataLength);
        Assert.IsNull(reply.Drive);
    }

    [TestMethod]
    public async Task GrowUsnJournal_RepliesWithRequestId()
    {
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            GrowUsnJournal = (drive, maximumSize, allocationDelta) =>
            {
                Assert.AreEqual(new DriveKey('D'), drive);
                return new UsnJournalSettings { MaximumSize = maximumSize, AllocationDelta = allocationDelta };
            }
        });
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteGrowUsnJournal(writer, 9, 'D', 65536, 4096));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.UsnJournalSettings, reply.Kind);
        Assert.AreEqual(9u, reply.RequestId);
        Assert.AreEqual(65536L, reply.JournalMaximumSize);
        Assert.AreEqual(4096L, reply.JournalAllocationDelta);
    }

    [TestMethod]
    public async Task QueryVolume_SourceThrows_RepliesErrorWithRequestId()
    {
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = _ => throw new UnauthorizedAccessException("access denied")
        });
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 3, 'C'));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, reply.Kind);
        Assert.AreEqual(3u, reply.RequestId);
        Assert.AreEqual("access denied", reply.Message);
    }

    [TestMethod]
    public async Task ControlRequests_RunConcurrently()
    {
        var gate = new TestGate();
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = drive =>
            {
                if (drive.Letter == 'C')
                {
                    gate.MarkEntered();
                    gate.WaitForReleaseAsync(CancellationToken.None).Wait(HangGuard, TestContext.CancellationToken);
                }

                return Volume;
            }
        });
        await using var harness = new HostChannelHarness(host);

        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 1, 'C'));
        await gate.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 2, 'D'));

        var first = await harness.ReadControlAsync();
        gate.Release();
        var second = await harness.ReadControlAsync();

        Assert.AreEqual(2u, first.RequestId, "The ungated query must be answered while the first one waits.");
        Assert.AreEqual(1u, second.RequestId);
    }

    [TestMethod]
    public async Task OpenChannel_ConnectsNamedPipeAndReplies()
    {
        var pipeName = "mftlib-broker-" + Guid.NewGuid().ToString("N");
        await using var server = BrokerPipeAccess.CreateServer(pipeName);
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            WatchDrive = IdleWatch
        });
        await using var harness = new HostChannelHarness(host,
            connectChannel: (driveName, driveToken) => DefaultElevatedEntryRunner.ConnectDrivePipeAsync(
                driveName, BrokerLiveness.ElevatedConnectTimeout, TimeProvider.System, driveToken));

        await harness.SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, 5, 'C', pipeName));
        await server.WaitForConnectionAsync(TestContext.CancellationToken).WaitAsync(HangGuard, TestContext.CancellationToken);
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
        var clock = new TimerSignalingClock();
        var connection = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectToken = new TaskCompletionSource<CancellationToken>();
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            TimeProvider = clock
        });
        await using var harness = new HostChannelHarness(host, connectChannel: (_, token) =>
        {
            connectToken.TrySetResult(token);
            return connection.Task;
        });

        await harness.SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, 12, 'C', harness.NextPipeName('C')));
        var reply = harness.ReadControlAsync();
        await AdvanceWhenTimerExistsAsync(clock, BrokerLiveness.ChannelConnectTimeout, 1, reply);

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
        var clock = new TimerSignalingClock();
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            TimeProvider = clock
        });
        var connectionsReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new HostChannelHarness(host, connectionsReleased: connectionsReleased.Task);
        var pipe = await OpenChannelAfterConnectBoundAsync(harness, clock, connectionsReleased, 'C');

        var frame = HostChannelHarness.ReadFrameAsync(pipe);
        await AdvanceWhenTimerExistsAsync(clock, BrokerLiveness.FirstRequestTimeout, 2, frame);

        Assert.AreEqual(BrokerFrameKind.Error, (await frame)?.Kind);
        Assert.AreEqual(0u, (await frame)?.RequestId);
        Assert.IsNull(await HostChannelHarness.ReadFrameAsync(pipe), "The host must close the channel.");
    }

    [TestMethod]
    public async Task OpenChannel_ConnectorThrows_RepliesErrorWithRequestId()
    {
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host,
            connectChannel: (_, _) => throw new IOException("pipe not found"));

        await harness.SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, 77, 'C', harness.NextPipeName('C')));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, reply.Kind);
        Assert.AreEqual(77u, reply.RequestId);
        Assert.Contains("pipe not found", reply.Message!);
    }

    [TestMethod]
    public async Task DriveChannel_UnknownFirstFrame_WritesErrorAndCloses()
    {
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation
        });
        await using var harness = new HostChannelHarness(host);
        var pipe = await harness.OpenChannelAsync('C');

        await HostChannelHarness.WriteFrameAsync(pipe, BrokerProtocol.WriteCaughtUp);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.HasCount(1, frames);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.Contains("CaughtUp", frames[0].Message!);
    }

    [TestMethod]
    [DataRow("truncated section name")]
    [DataRow("negative name count")]
    [DataRow("name count beyond the payload")]
    public async Task DriveChannel_MalformedRequestPayload_WritesErrorAndOtherRequestsContinue(string malformation)
    {
        var payload = new List<byte>();
        payload.AddRange(BitConverter.GetBytes(malformation == "truncated section name" ? 100 : 0));
        if (malformation != "truncated section name")
        {
            payload.AddRange(BitConverter.GetBytes(0)); // IncludeFreed
            payload.AddRange(BitConverter.GetBytes(malformation == "negative name count" ? -2 : int.MaxValue));
        }
        else
        {
            payload.AddRange(Encoding.Unicode.GetBytes("C"));
        }

        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = _ => Volume
        });
        await using var harness = new HostChannelHarness(host, new CountingBlockSectionWriter());
        var pipe = await harness.OpenChannelAsync('C');

        await HostChannelHarness.WriteFrameAsync(pipe, writer =>
        {
            var frame = writer.GetSpan(5 + payload.Count);
            BitConverter.TryWriteBytes(frame, 1 + payload.Count);
            frame[4] = (byte)BrokerFrameKind.ArmAndScan;
            payload.ToArray().CopyTo(frame[5..]);
            writer.Advance(5 + payload.Count);
        });
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.HasCount(1, frames);
        Assert.AreEqual(BrokerFrameKind.Error, frames[0].Kind);
        Assert.Contains("malformed", frames[0].Message!);
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 8, 'C'));
        Assert.AreEqual(BrokerFrameKind.VolumeInfo, (await harness.ReadControlAsync()).Kind,
            "The session keeps serving after one channel's malformed request.");
    }

    [TestMethod]
    public async Task OpenChannel_ConnectorThrowsOperationCanceled_RepliesErrorWithRequestId()
    {
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = _ => Volume
        });
        await using var harness = new HostChannelHarness(host,
            connectChannel: (_, _) => Task.FromCanceled<Stream>(new CancellationToken(true)));

        await harness.SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, 21, 'C', harness.NextPipeName('C')));
        var reply = await harness.ReadControlAsync();
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 22, 'C'));
        var next = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, reply.Kind);
        Assert.AreEqual(21u, reply.RequestId);
        Assert.AreEqual(22u, next.RequestId);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("other-broker-0123456789abcdef0123456789abcdef")]
    [DataRow("MFTLIB-BROKER-0123456789abcdef0123456789abcdef")]
    [DataRow(@"mftlib-broker-0123456789abcdef\123456789abcdef")]
    [DataRow(@"Global\mftlib-broker-0123456789abcdef0123456789abcdef")]
    [DataRow(@"Local\mftlib-broker-0123456789abcdef0123456789abcdef")]
    [DataRow("mftlib-broker-0123456789abcdef0123456789abcde")]
    [DataRow("mftlib-broker-0123456789abcdef0123456789abcdef0")]
    [DataRow("mftlib-broker-0123456789abcdef0123456789abcdeg")]
    [DataRow("mftlib-broker-0123456789abcdef0123456789abcdef-C-1")]
    public async Task OpenChannel_InvalidPipeName_RepliesErrorWithoutConnectingAndControlContinues(string invalidPipeName)
    {
        var connectorCalls = 0;
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = _ => Volume
        });
        await using var harness = new HostChannelHarness(host,
            connectChannel: (_, _) =>
            {
                Interlocked.Increment(ref connectorCalls);
                return Task.FromException<Stream>(new InvalidOperationException("Connector should not be called."));
            });

        await harness.SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, 31, 'C', invalidPipeName));
        var reply = await harness.ReadControlAsync();
        await harness.SendControlAsync(writer => BrokerProtocol.WriteQueryVolume(writer, 32, 'C'));
        var next = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.Error, reply.Kind);
        Assert.AreEqual(31u, reply.RequestId);
        Assert.Contains("invalid pipe name", reply.Message!);
        Assert.AreEqual(0, connectorCalls);
        Assert.AreEqual(BrokerFrameKind.VolumeInfo, next.Kind);
        Assert.AreEqual(32u, next.RequestId);
    }

    [TestMethod]
    [DataRow("mftlib-broker-0123456789abcdef0123456789abcdef")]
    [DataRow("mftlib-broker-ABCDEF0123456789ABCDEF0123456789")]
    [DataRow("mftlib-broker-0123456789abcdefABCDEF0123456789")]
    public async Task OpenChannel_ValidHexPipeNames_ConnectAndReplyChannelOpened(string validPipeName)
    {
        var connectedNames = new List<string>();
        var host = BrokerHostFactory.Create(new()
        {
            ScanDrive = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            QueryVolumeInformation = BrokerHostFactory.NoVolumeInformation,
            WatchDrive = IdleWatch
        });
        await using var harness = new HostChannelHarness(host, wrapDrivePipe: (pipeName, stream) =>
        {
            connectedNames.Add(pipeName);
            return stream;
        });

        await harness.SendControlAsync(writer => BrokerProtocol.WriteOpenChannel(writer, 45, 'C', validPipeName));
        var reply = await harness.ReadControlAsync();

        Assert.AreEqual(BrokerFrameKind.ChannelOpened, reply.Kind);
        Assert.AreEqual(45u, reply.RequestId);
        Assert.AreSequenceEqual([validPipeName], connectedNames);
    }
    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> IdleWatch(
        DriveKey drive, UsnJournalCursor since, ISourceOperationReporter operation,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        operation.WaitingOnVolume();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    // Waits until the host has created the timer under test, then advances the clock to its due
    // time, so the timer fires exactly once and no step depends on when the host reached its wait.
    // Timers with the same due time are numbered in creation order, from 1.
    static async Task AdvanceWhenTimerExistsAsync(TimerSignalingClock clock, TimeSpan dueTime, int occurrence, Task task)
    {
        await clock.TimerCreated(dueTime, occurrence).WaitAsync(HangGuard);
        clock.Advance(dueTime);
        await task.WaitAsync(HangGuard);
    }

    // Opens a channel whose connect bound is the first 30 second timer on the host's clock, so the
    // channel's first-request bound is the second. The host creates the connect bound only if the
    // connection is still pending when it starts waiting, so the harness holds the connection
    // until that timer exists.
    static async Task<Stream> OpenChannelAfterConnectBoundAsync(HostChannelHarness harness, TimerSignalingClock clock,
        TaskCompletionSource connectionsReleased, DriveKey drive)
    {
        var open = harness.OpenChannelAsync(drive);
        await clock.TimerCreated(BrokerLiveness.ChannelConnectTimeout).WaitAsync(HangGuard);
        connectionsReleased.SetResult();
        return await open.WaitAsync(HangGuard);
    }
}
