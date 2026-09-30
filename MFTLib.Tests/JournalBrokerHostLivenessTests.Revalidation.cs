using MFTLib.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// A heartbeat visit decides from the pipe's state as it stands once the visit holds the pipe, and
// a processing pipe that is making progress heartbeats instead of going silent.
public partial class JournalBrokerHostLivenessTests
{
    [TestMethod]
    public async Task Visit_ProgressPublishedAfterVisitBegan_WritesNoStalledAndKeepsChannel()
    {
        var clock = new FakeTimeProvider();
        using var owner = new CancellationTokenSource();
        using var stream = new MemoryStream();
        var pipe = new HostPipeWriter(stream, "pipe", clock, heartbeatsWhenIdle: false, owner, null);
        pipe.Processing("catch-up");
        clock.Advance(BrokerLiveness.ProcessingLimit);

        await VisitWithWindowAsync(pipe, clock, () =>
        {
            pipe.Processing("catch-up");
            return Task.CompletedTask;
        });

        CollectionAssert.AreEqual(new[] { BrokerFrameKind.Heartbeat }, FrameKinds(stream),
            "Progress published inside the visit's window restarts the clock: a heartbeat, not Stalled.");
        Assert.IsFalse(owner.IsCancellationRequested, "A progressing channel is not cancelled.");
    }

    [TestMethod]
    public async Task Visit_FrameCompletedAfterVisitBegan_WritesNoStalledAndKeepsChannel()
    {
        var clock = new FakeTimeProvider();
        using var owner = new CancellationTokenSource();
        using var stream = new MemoryStream();
        var pipe = new HostPipeWriter(stream, "pipe", clock, heartbeatsWhenIdle: false, owner, null);
        pipe.Processing("catch-up");
        clock.Advance(BrokerLiveness.ProcessingLimit);

        await VisitWithWindowAsync(pipe, clock,
            () => pipe.WriteFrameAsync(BrokerProtocol.WriteCaughtUp, CancellationToken.None));

        CollectionAssert.AreEqual(new[] { BrokerFrameKind.CaughtUp }, FrameKinds(stream),
            "A frame completed inside the visit's window counts as this interval's write: no Stalled.");
        Assert.IsFalse(owner.IsCancellationRequested, "A channel that just wrote is not cancelled.");
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
            return ([JournalEntryFactory.Create(1, since.NextUsn, "entry.txt")],
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

        await calls[0].Entered.WaitAsync(HostChannelHarness.HangGuard);
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
                await calls[index + 1].Entered.WaitAsync(HostChannelHarness.HangGuard);
            }
        }

        var rest = await HostChannelHarness.ReadToEndAsync(pipe, includeHeartbeats: true);
        Assert.IsFalse(rest.Any(frame => frame.Kind == BrokerFrameKind.Stalled), "60 seconds of progress never stalls.");
        Assert.AreEqual(BrokerFrameKind.JournalBatch, rest[^1].Kind);
        Assert.AreEqual(6, rest[^1].Entries.Length);
    }

    // Runs one visit on another thread and performs the window action after the visit began but
    // before it takes the pipe's write lock.
    static async Task VisitWithWindowAsync(HostPipeWriter pipe, FakeTimeProvider clock, Func<Task> window)
    {
        var began = new TestGate();
        pipe.VisitStartingForTest = () =>
        {
            began.MarkEntered();
            began.WaitForRelease();
        };
        var now = clock.GetUtcNow();
        var visit = Task.Run(() => pipe.Visit(now));
        await began.Entered.WaitAsync(HostChannelHarness.HangGuard);
        await window().WaitAsync(HostChannelHarness.HangGuard);
        began.Release();
        await visit.WaitAsync(HostChannelHarness.HangGuard);
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
