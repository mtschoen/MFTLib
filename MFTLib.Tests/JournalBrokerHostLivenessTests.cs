using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Tests.TestSupport;
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
        await using var harness = new HostChannelHarness(liveness.Host(watchDrive: NeverYields));
        var pipe = await harness.OpenWatchChannelAsync('C', BehindTip);
        await liveness.WhenPublished(DriveTag('C', 1), ChannelOperationKind.WaitingOnVolume);

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
        await using var harness = new HostChannelHarness(liveness.Host(scanDrive: (_, _, operation, _, token) =>
        {
            operation.Processing("wedged step");
            entered.MarkEntered();
            token.WaitHandle.WaitOne(HostChannelHarness.HangGuard);
            token.ThrowIfCancellationRequested();
            return [];
        }), sectionWriter);
        var pipe = await harness.OpenScanChannelAsync('C');
        await entered.Entered.WaitAsync(HostChannelHarness.HangGuard);

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
        await using var harness = new HostChannelHarness(liveness.Host(scanDrive: (_, _, operation, _, _) =>
        {
            foreach (var step in steps)
            {
                operation.Processing("MFT parse");
                step.MarkEntered();
                step.WaitForRelease();
            }

            return [];
        }), sectionWriter);
        var pipe = await harness.OpenScanChannelAsync('C');

        // Progress every 10 seconds for 120 seconds: each step is published, then two intervals pass.
        foreach (var step in steps)
        {
            await step.Entered.WaitAsync(HostChannelHarness.HangGuard);
            await liveness.AdvanceOneIntervalAsync();
            await liveness.AdvanceOneIntervalAsync();
            step.Release();
        }

        var frames = await HostChannelHarness.ReadToEndAsync(pipe, includeHeartbeats: true);

        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.Stalled));
        Assert.AreEqual(BrokerFrameKind.JournalBatch, frames[^1].Kind, "The scan completes normally.");
    }

    [TestMethod]
    public async Task QueuedScan_WaitingForAdmission_Heartbeats()
    {
        var liveness = new Liveness();
        var admitted = new TestGate();
        var host = liveness.Host(processorCount: 1, scanDrive: (drive, _, operation, _, token) =>
        {
            if (drive == "C")
            {
                // Holds the only parse thread, waiting on its volume, which never stalls.
                operation.WaitingOnVolume();
                admitted.MarkEntered();
                token.WaitHandle.WaitOne(HostChannelHarness.HangGuard);
                token.ThrowIfCancellationRequested();
            }

            return [];
        });
        using var sectionWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, sectionWriter);
        await harness.OpenScanChannelAsync('C');
        await admitted.Entered.WaitAsync(HostChannelHarness.HangGuard);
        var queued = await harness.OpenScanChannelAsync('D');
        await liveness.WhenPublished(DriveTag('D', 1), ChannelOperationKind.Queued);

        for (var interval = 0; interval < 12; interval++)
        {
            await liveness.AdvanceOneIntervalAsync();
            Assert.AreEqual(BrokerFrameKind.Heartbeat, await ReadIncludingHeartbeatsAsync(queued),
                $"The queued scan's pipe heartbeats on interval {interval + 1}.");
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

    static string DriveTag(char drive, int sequence) => BrokerDiagnostics.DriveChannel(drive, sequence);

    static async Task<BrokerFrameKind?> ReadIncludingHeartbeatsAsync(Stream pipe)
    {
        return (await HostChannelHarness.ReadFrameAsync(pipe, includeHeartbeats: true))?.Kind;
    }

    // A watch source that never yields: the drive's journal stays quiet until the pipe closes.
    static async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> NeverYields(
        string drive, UsnJournalCursor since, IBrokerOperationReporter operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    // One host on a fake clock, with the liveness test hooks installed.
    sealed class Liveness
    {
        // A channel rather than a semaphore: the sender may still report a visit while the test is
        // tearing down, and a channel has nothing to dispose.
        readonly Channel<bool> _visits = Channel.CreateUnbounded<bool>();
        readonly List<(string Tag, ChannelOperationState State)> _published = [];
        readonly List<(string Tag, ChannelOperationKind Kind, TaskCompletionSource Reached)> _waiters = [];
        readonly Lock _gate = new();

        public FakeTimeProvider Clock { get; } = new();

        public bool SenderIsThreadPoolThread { get; private set; } = true;

        public bool SenderIsBackground { get; private set; }

        public IReadOnlyList<(string Tag, ChannelOperationState State)> Published
        {
            get
            {
                lock (_gate)
                {
                    return _published.ToArray();
                }
            }
        }

        public JournalBrokerHost Host(UsnJournalCatchUpSource? readJournal = null, JournalBatchSource? watchDrive = null,
            MftRecordBatchSource? scanDrive = null, int processorCount = 4)
        {
            return new JournalBrokerHost(
                _ => Tip,
                scanDrive ?? ((_, _, _, _, _) => []),
                readJournal ?? ((_, since, _) => ([], since)),
                watchDrive,
                processorCount: processorCount,
                timeProvider: Clock)
            {
                OperationStatePublishedForTest = Record,
                HeartbeatVisitedForTest = () =>
                {
                    SenderIsThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
                    SenderIsBackground = Thread.CurrentThread.IsBackground;
                    _visits.Writer.TryWrite(true);
                }
            };
        }

        // Each advance fires the sender's timer once; the next advance waits for that visit, so
        // two ticks never merge into one visit.
        public Task<bool> AdvanceOneIntervalAsync()
        {
            Clock.Advance(BrokerLiveness.HeartbeatInterval);
            return _visits.Reader.ReadAsync().AsTask().WaitAsync(HostChannelHarness.HangGuard);
        }

        public Task WhenPublished(string tag, ChannelOperationKind kind)
        {
            lock (_gate)
            {
                if (_published.Any(entry => entry.Tag == tag && entry.State.Kind == kind))
                {
                    return Task.CompletedTask;
                }

                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((tag, kind, reached));
                return reached.Task.WaitAsync(HostChannelHarness.HangGuard);
            }
        }

        // Publications of the step that repeat the step before them on the same pipe: each one is
        // a restart of the progress clock without a change of state.
        public int Republishes(string tag, string step)
        {
            var steps = Published.Where(entry => entry.Tag == tag).Select(entry => entry.State.Step).ToList();
            return steps.Zip(steps.Skip(1)).Count(pair => pair.First == step && pair.Second == step);
        }

        void Record(string tag, ChannelOperationState state)
        {
            lock (_gate)
            {
                _published.Add((tag, state));
                foreach (var waiter in _waiters.Where(waiter => waiter.Tag == tag && waiter.Kind == state.Kind))
                {
                    waiter.Reached.TrySetResult();
                }
            }
        }
    }
}
