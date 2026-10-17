using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Host liveness: the heartbeat sender, the processing watchdog and the progress republishes a
// scan makes. Every limit reads a FakeTimeProvider, and each test advances it one heartbeat
// interval at a time, waiting for the sender's visit before the next advance. The catch-up cases
// install JournalCheckpointCheck's process-wide journal override, so the class runs serially.
[TestClass]
[DoNotParallelize]
public partial class JournalBrokerHostLivenessTests
{
    static readonly UsnJournalCursor Tip = new(7, 1000);

    // Behind the tip, so a watch armed here has a backlog and writes no leading CaughtUp: the
    // pipe stays silent until the heartbeat sender writes to it.
    static readonly UsnJournalCursor BehindTip = new(7, 500);

    [TestMethod]
    public async Task IdleWatch_WaitingOnVolume_HeartbeatsAndNeverStalls()
    {
        var liveness = new Liveness();
        var entered = new TestGate();
        await using var harness = new HostChannelHarness(liveness.Host(
            watchDrive: (_, _, _, token) => QuietWatchAsync(entered, token)));
        var pipe = await harness.OpenWatchChannelAsync('C', BehindTip);
        await entered.Entered.WaitAsync(HangGuard);

        for (var interval = 0; interval < 24; interval++)
        {
            await liveness.AdvanceOneIntervalAsync();
            Assert.AreEqual(BrokerFrameKind.Heartbeat, await ReadIncludingHeartbeatsAsync(pipe),
                $"Interval {interval + 1} of 120 seconds");
        }

        await harness.CloseControlAsync();
        Assert.AreEqual(0, (await HostChannelHarness.ReadToEndAsync(pipe, includeHeartbeats: true)).Count,
            "An idle watch writes 24 heartbeats in 120 seconds and nothing else: no Stalled.");
    }

    [TestMethod]
    public async Task WedgedProcessing_WritesStalledNamingStepAndCloses()
    {
        var liveness = new Liveness();
        var entered = new TestGate();
        using var sectionWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(liveness.Host(scanDrive: (_, _, reporting, _, _, token) =>
        {
            reporting.Operation!.Processing("wedged step");
            entered.MarkEntered();
            token.WaitHandle.WaitOne(HangGuard);
            token.ThrowIfCancellationRequested();
            return [];
        }), sectionWriter);
        var pipe = await harness.OpenScanChannelAsync('C');
        await entered.Entered.WaitAsync(HangGuard);

        for (var interval = 0; interval < 6; interval++)
        {
            await liveness.AdvanceOneIntervalAsync();
        }

        var frames = await HostChannelHarness.ReadToEndAsync(pipe, includeHeartbeats: true);

        // Within the limit the processing pipe heartbeats; past it, Stalled is the last frame and the
        // host closes the pipe.
        Assert.AreEqual(BrokerFrameKind.Stalled, frames[^1].Kind, "Stalled is the last frame, then EOF.");
        CollectionAssert.AreEqual(new[] { BrokerFrameKind.Cursor, BrokerFrameKind.Stalled },
            frames.Where(frame => frame.Kind != BrokerFrameKind.Heartbeat).Select(frame => frame.Kind).ToArray());
        StringAssert.Contains(frames[^1].Message, "wedged step");
        StringAssert.Contains(frames[^1].Message, "30 seconds");
    }

    [TestMethod]
    public async Task ProcessingWithProgress_NeverStalls()
    {
        var liveness = new Liveness();
        var steps = Enumerable.Range(0, 12).Select(_ => new TestGate()).ToArray();
        using var sectionWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(liveness.Host(scanDrive: (_, _, reporting, _, _, _) =>
        {
            foreach (var step in steps)
            {
                reporting.Operation!.Processing("MFT parse");
                step.MarkEntered();
                step.WaitForRelease();
            }

            return [];
        }), sectionWriter);
        var pipe = await harness.OpenScanChannelAsync('C');

        // Progress every 10 seconds for 120 seconds: each step is published, then two intervals pass.
        foreach (var step in steps)
        {
            await step.Entered.WaitAsync(HangGuard);
            await liveness.AdvanceOneIntervalAsync();
            await liveness.AdvanceOneIntervalAsync();
            step.Release();
        }

        var frames = await HostChannelHarness.ReadToEndAsync(pipe, includeHeartbeats: true);

        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Stalled));
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind, "The scan completes normally.");
    }

    [TestMethod]
    public async Task QueuedScan_WaitingForAdmission_Heartbeats()
    {
        var liveness = new Liveness();
        var admitted = new TestGate();
        var queuedSourceEntered = new TestGate();
        var host = liveness.Host(processorCount: 1, scanDrive: (drive, _, reporting, _, _, token) =>
        {
            if (drive == 'C')
            {
                // Holds the only parse thread, waiting on its volume, which never stalls.
                reporting.Operation!.WaitingOnVolume();
                admitted.MarkEntered();
                token.WaitHandle.WaitOne(HangGuard);
                token.ThrowIfCancellationRequested();
            }
            else
            {
                queuedSourceEntered.MarkEntered();
            }

            return [];
        });
        using var sectionWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, sectionWriter);
        await harness.OpenScanChannelAsync('C');
        await admitted.Entered.WaitAsync(HangGuard);
        var queued = await harness.OpenScanChannelAsync('D');
        await WaitForQueuedScanAsync(host);

        for (var interval = 0; interval < 12; interval++)
        {
            await liveness.AdvanceOneIntervalAsync();
            Assert.AreEqual(BrokerFrameKind.Heartbeat, await ReadIncludingHeartbeatsAsync(queued),
                $"The queued scan's pipe heartbeats on interval {interval + 1}.");
            Assert.AreEqual(1, host.ParseThreads.RunningScanCount);
            Assert.AreEqual(1, host.ParseThreads.QueuedScanCount);
            Assert.IsFalse(queuedSourceEntered.Entered.IsCompleted);
        }
    }

    [TestMethod]
    public async Task IdleSession_NoRequestsNoWatches_StaysAlivePastStallLimit()
    {
        var liveness = new Liveness();
        await using var harness = new HostChannelHarness(liveness.Host());

        for (var interval = 0; interval < 60; interval++)
        {
            await liveness.AdvanceOneIntervalAsync();
            Assert.AreEqual(BrokerFrameKind.Heartbeat, (await harness.ReadControlAsync(includeHeartbeats: true)).Kind,
                $"The idle control pipe heartbeats on interval {interval + 1} of 300 seconds.");
        }

        Assert.IsFalse(harness.Serve.IsCompleted, "The session is still serving.");
    }

    [TestMethod]
    public async Task HeartbeatSender_RunsOnDedicatedThread()
    {
        var liveness = new Liveness();
        await using var harness = new HostChannelHarness(liveness.Host());

        await liveness.AdvanceOneIntervalAsync();

        Assert.IsFalse(liveness.SenderIsThreadPoolThread, "The sender must not run on the thread pool.");
        Assert.IsTrue(liveness.SenderIsBackground, "The sender thread must not keep the process alive.");
    }

    static async Task<BrokerFrameKind?> ReadIncludingHeartbeatsAsync(Stream pipe)
    {
        return (await HostChannelHarness.ReadFrameAsync(pipe, includeHeartbeats: true))?.Kind;
    }

    static async Task WaitForQueuedScanAsync(JournalBrokerHost host)
    {
        using var guard = new CancellationTokenSource(HangGuard);
        while (host.ParseThreads.QueuedScanCount != 1)
        {
            guard.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> QuietWatchAsync(
        TestGate entered, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        entered.MarkEntered();
        await entered.WaitForReleaseAsync(cancellationToken);
        yield break;
    }

    // One host on a fake clock, with a completed-visit observation.
    sealed class Liveness
    {
        // A channel rather than a semaphore: the sender may still report a visit while the test is
        // tearing down, and a channel has nothing to dispose.
        readonly Channel<bool> _visits = Channel.CreateUnbounded<bool>();

        public FakeTimeProvider Clock { get; } = new();

        public bool SenderIsThreadPoolThread { get; private set; } = true;

        public bool SenderIsBackground { get; private set; }

        public JournalBrokerHost Host(
            Func<DriveKey, UsnJournalCursor, int, (UsnJournalEntry[] Entries, UsnJournalCursor Updated)>? readJournal = null,
            Func<DriveKey, UsnJournalCursor, ISourceOperationReporter, CancellationToken, IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>>? watchDrive = null,
            MftRecordBatchSource? scanDrive = null, int processorCount = 4)
        {
            var host = new JournalBrokerHost(
                new ScriptedVolumeAccess
                {
                    CursorQuery = _ => Tip,
                    RecordBatches = scanDrive ?? ((_, _, _, _, _, _) => []),
                    JournalRead = readJournal ?? ((_, since, _) => ([], since)),
                    JournalWatch = watchDrive ?? ScriptedVolumeAccess.UnsupportedWatch
                },
                processorCount: processorCount,
                timeProvider: Clock);
            host.Hooks.HeartbeatVisited = () =>
            {
                SenderIsThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
                SenderIsBackground = Thread.CurrentThread.IsBackground;
                _visits.Writer.TryWrite(true);
            };
            return host;
        }

        // Each advance fires the sender's timer once; the next advance waits for that visit, so
        // two ticks never merge into one visit.
        public Task<bool> AdvanceOneIntervalAsync()
        {
            Clock.Advance(BrokerLiveness.HeartbeatInterval);
            return _visits.Reader.ReadAsync().AsTask().WaitAsync(HangGuard);
        }
    }
}
