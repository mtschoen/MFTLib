using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// A heartbeat visit decides from the pipe's state as it stands once the visit holds the pipe, and
// a processing pipe that is making progress heartbeats instead of going silent.
public partial class JournalBrokerHostLivenessTests
{
    // One sweep of the heartbeat sender visits the control pipe, then the quiet watch Z, then the wedged watch C, all
    // against the clock read when the sweep began. The sweep is made to wait inside Z's heartbeat write, after it
    // began and before C is visited, while the event under test lands on C; C must then be judged by what it holds
    // when it is visited, not by what it held when the sweep began. The final sweep is at the processing limit.
    [TestMethod]
    public async Task Sweep_ProgressPublishedAfterSweepBegan_WritesNoStalledAndKeepsChannel()
    {
        await using var scenario = await SweepScenario.StartAsync();

        await scenario.RunSweepsToTheLimitAsync(() => scenario.WedgedReporter.Processing("wedged step"));

        var frames = await scenario.FinishAsync();
        Assert.AreSequenceEqual(Enumerable.Repeat(BrokerFrameKind.Heartbeat, 6).ToArray(), frames.Select(frame => frame.Kind).ToArray(), $"Progress published after the sweep began restarts the clock: a heartbeat, not Stalled. Saw {string.Join(", ", frames.Select(frame => frame.Kind))}.");
    }

    [TestMethod]
    public async Task Sweep_FrameCompletedAfterSweepBegan_WritesNoStalledAndKeepsChannel()
    {
        await using var scenario = await SweepScenario.StartAsync();

        await scenario.RunSweepsToTheLimitAsync(() =>
        {
            scenario.Wedged.Release();
            TestGate.WaitSynchronously(scenario.BatchWritten.Entered);
        });

        var frames = await scenario.FinishAsync();
        Assert.AreSequenceEqual(
            Enumerable.Repeat(BrokerFrameKind.Heartbeat, 5).Append(BrokerFrameKind.JournalBatch).Append(BrokerFrameKind.CaughtUp).ToArray(), frames.Select(frame => frame.Kind).ToArray(), $"A frame completed after the sweep began is this interval's write: no Stalled and no sixth heartbeat. Saw {string.Join(", ", frames.Select(frame => frame.Kind))}.");
    }

    sealed class SweepScenario : IAsyncDisposable
    {
        readonly Liveness _liveness = new();
        readonly TestGate _quietEntered = new();
        readonly TestGate _wedgedEntered = new();
        HostChannelHarness _harness = null!;
        Stream _wedgedPipe = null!;
        Action? _duringSweep;

        public TestGate Wedged { get; } = new();

        public TestGate BatchWritten { get; } = new();

        public ISourceOperationReporter WedgedReporter { get; private set; } = null!;

        public static async Task<SweepScenario> StartAsync()
        {
            var scenario = new SweepScenario();
            scenario._harness = new HostChannelHarness(scenario._liveness.Host(watchDrive: scenario.WatchAsync),
                wrapDrivePipe: scenario.Wrap);
            await scenario._harness.OpenWatchChannelAsync('Z', BehindTip);
            await scenario._quietEntered.Entered.WaitAsync(HangGuard);
            scenario._wedgedPipe = await scenario._harness.OpenWatchChannelAsync('C', BehindTip);
            await scenario._wedgedEntered.Entered.WaitAsync(HangGuard);
            return scenario;
        }

        // Five sweeps within the limit, then the sweep at the limit with the event run inside it.
        public async Task RunSweepsToTheLimitAsync(Action duringFinalSweep)
        {
            for (var interval = 0; interval < 5; interval++)
            {
                await _liveness.AdvanceOneIntervalAsync();
            }

            _duringSweep = duringFinalSweep;
            await _liveness.AdvanceOneIntervalAsync();
            Assert.IsNull(_duringSweep, "The event ran inside the sweep, after it began.");
        }

        public async Task<List<BrokerFrame>> FinishAsync()
        {
            await _harness.CloseControlAsync();
            return await HostChannelHarness.ReadToEndAsync(_wedgedPipe, includeHeartbeats: true);
        }

        public ValueTask DisposeAsync()
        {
            Wedged.Release();
            return _harness.DisposeAsync();
        }

        IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchAsync(
            DriveKey drive, UsnJournalCursor since, ISourceOperationReporter reporter, CancellationToken cancellationToken)
        {
            if (drive == 'Z')
            {
                return QuietWatchAsync(_quietEntered, cancellationToken);
            }

            WedgedReporter = reporter;
            return WedgedWatchAsync(reporter, cancellationToken);
        }

        // Processing since the clock's start, then one batch once released.
        async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WedgedWatchAsync(
            ISourceOperationReporter reporter, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            reporter.Processing("wedged step");
            _wedgedEntered.MarkEntered();
            await Wedged.WaitForReleaseAsync(cancellationToken);
            yield return ([JournalEntries.Create(1, Tip.NextUsn, "entry.txt")], Tip with { NextUsn = Tip.NextUsn + 100 });
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        ObservedWriteStream Wrap(string pipeName, Stream hostEnd)
        {
            if (pipeName.StartsWith("harness-Z-", StringComparison.Ordinal))
            {
                return new ObservedWriteStream(hostEnd, kind =>
                {
                    if (kind == BrokerFrameKind.Heartbeat && Interlocked.Exchange(ref _duringSweep, null) is { } action)
                    {
                        action();
                    }
                }, afterWrite: false);
            }

            return new ObservedWriteStream(hostEnd, kind =>
            {
                if (kind == BrokerFrameKind.JournalBatch)
                {
                    BatchWritten.MarkEntered();
                }
            }, afterWrite: true);
        }
    }

    // Reports each written frame's kind on the writing thread, before or after the write reaches the pipe.
    sealed class ObservedWriteStream(Stream inner, Action<BrokerFrameKind> observe, bool afterWrite) : DelegatingStream(inner)
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var kind = (BrokerFrameKind)buffer.Span[4];
            if (!afterWrite)
            {
                observe(kind);
                return base.WriteAsync(buffer, cancellationToken);
            }

            var write = base.WriteAsync(buffer, cancellationToken);
            return write.IsCompletedSuccessfully ? ObserveCompleted(kind) : ObserveAfter(write, kind);
        }

        ValueTask ObserveCompleted(BrokerFrameKind kind)
        {
            observe(kind);
            return ValueTask.CompletedTask;
        }

        async ValueTask ObserveAfter(ValueTask write, BrokerFrameKind kind)
        {
            await write;
            observe(kind);
        }
    }

    [TestMethod]
    public async Task ProgressingCatchUp_NoFrameForSixtySeconds_HeartbeatsAndNeverStalls()
    {
        var liveness = new Liveness();
        var calls = Enumerable.Range(0, 6).Select(_ => new TestGate()).ToArray();
        var call = 0;
        var host = liveness.Host(readJournal: (_, since, _) =>
        {
            if (call == calls.Length)
            {
                return ([], since);
            }

            // Each bounded read takes 10 seconds and returns a chunk, so the step republishes every
            // 10 seconds while the pipe writes no frame.
            var gate = calls[call++];
            gate.MarkEntered();
            gate.WaitForRelease();
            return ([JournalEntries.Create(1, since.NextUsn, "entry.txt")],
                since with { NextUsn = since.NextUsn + 100 });
        });
        using var sectionWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, sectionWriter);
        try
        {
            await AssertCatchUpHeartbeatsAsync(harness, liveness, calls);
        }
        finally
        {
            // A failed assertion must not leave the source parked, or disposal masks the failure.
            foreach (var gate in calls)
            {
                gate.Release();
            }
        }
    }

    static async Task AssertCatchUpHeartbeatsAsync(HostChannelHarness harness, Liveness liveness, TestGate[] calls)
    {
        var pipe = await harness.OpenScanChannelAsync('C');
        foreach (var expected in new[] { BrokerFrameKind.Cursor, BrokerFrameKind.ScanProgress, BrokerFrameKind.ScanReady })
        {
            Assert.AreEqual(expected, await ReadIncludingHeartbeatsAsync(pipe));
        }

        await calls[0].Entered.WaitAsync(HangGuard);
        // The first visit sees the scan's frames and skips the pipe.
        await liveness.AdvanceOneIntervalAsync();

        for (var index = 0; index < calls.Length; index++)
        {
            for (var interval = 0; interval < 2; interval++)
            {
                await liveness.AdvanceOneIntervalAsync();
                Assert.AreEqual(BrokerFrameKind.Heartbeat, await ReadIncludingHeartbeatsAsync(pipe),
                    $"Catch-up read {index + 1}, interval {interval + 1}: a progressing pipe heartbeats.");
            }

            calls[index].Release();
            if (index + 1 < calls.Length)
            {
                await calls[index + 1].Entered.WaitAsync(HangGuard);
            }
        }

        var rest = await HostChannelHarness.ReadToEndAsync(pipe, includeHeartbeats: true);
        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.Stalled, rest, "60 seconds of progress never stalls.");
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, rest[^1].Kind);
        Assert.IsEmpty(rest[^1].Entries, "The chunks the catch-up read stay on the host.");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void Visit_ProcessingWithoutFrames_HeartbeatsUntilProgressLimit(
        int extraIntervalsAfterLimit)
    {
        var clock = new FakeTimeProvider();
        using var owner = new CancellationTokenSource();
        using var stream = new MemoryStream();
        var pipe = new HostPipeWriter(stream, "pipe", clock, heartbeatsWhenIdle: false, owner);
        try
        {
            pipe.Processing("catch-up");
            clock.Advance(BrokerLiveness.ProcessingLimit - BrokerLiveness.HeartbeatInterval);
            Assert.AreEqual(0L, stream.Length, "Publishing progress writes no operation frame.");
            pipe.Visit(clock.GetUtcNow());
            Assert.AreSequenceEqual(new[] { BrokerFrameKind.Heartbeat }, FrameKinds(stream));
            Assert.IsFalse(owner.IsCancellationRequested);

            pipe.Processing("catch-up");
            clock.Advance(2 * BrokerLiveness.HeartbeatInterval);
            pipe.Visit(clock.GetUtcNow());
            Assert.AreSequenceEqual(
                new[] { BrokerFrameKind.Heartbeat, BrokerFrameKind.Heartbeat }, FrameKinds(stream), "Republished progress keeps a no-frame operation alive beyond its original deadline.");
            Assert.IsFalse(owner.IsCancellationRequested);

            clock.Advance(BrokerLiveness.ProcessingLimit - 2 * BrokerLiveness.HeartbeatInterval
                + extraIntervalsAfterLimit * BrokerLiveness.HeartbeatInterval);
            pipe.Visit(clock.GetUtcNow());
            Assert.AreSequenceEqual(
                new[] { BrokerFrameKind.Heartbeat, BrokerFrameKind.Heartbeat, BrokerFrameKind.Stalled }, FrameKinds(stream), "Heartbeats do not reset the processing progress clock.");
            Assert.IsTrue(owner.IsCancellationRequested);
        }
        finally
        {
            pipe.Close();
        }
    }

    static BrokerFrameKind[] FrameKinds(MemoryStream stream)
    {
        var bytes = stream.ToArray();
        var kinds = new List<BrokerFrameKind>();
        for (var offset = 0; offset < bytes.Length;)
        {
            kinds.Add(BrokerProtocol.ReadFrame(bytes.AsSpan(offset), out var consumed).Kind);
            offset += consumed;
        }

        return kinds.ToArray();
    }
}
