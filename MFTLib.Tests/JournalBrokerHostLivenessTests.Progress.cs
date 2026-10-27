using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// Held pipe writes, bounded catch-up reads and ranged block flushes.
public partial class JournalBrokerHostLivenessTests
{
    [TestMethod]
    public async Task BlockedWriteOnX_DoesNotDelayHeartbeatsOnY_AndSkipsXsHeartbeatsWhileItsWriteIsInFlight()
    {
        var liveness = new Liveness();
        var held = new HeldWrites();
        var enteredX = new TestGate();
        var enteredY = new TestGate();
        var host = liveness.Host(watchDrive: (drive, _, _, token) =>
            QuietWatchAsync(drive == 'X' ? enteredX : enteredY, token));
        await using var harness = HarnessHoldingWritesTo('X', host, held);
        var blocked = await harness.OpenWatchChannelAsync('X', BehindTip);
        var healthy = await harness.OpenWatchChannelAsync('Y', BehindTip);
        await Task.WhenAll(enteredX.Entered, enteredY.Entered).WaitAsync(HangGuard, TestContext.CancellationToken);

        // Well past the client's 30-second stall limit.
        for (var interval = 0; interval < 10; interval++)
        {
            await liveness.AdvanceOneIntervalAsync();
            if (interval == 0)
            {
                Assert.AreEqual(1, held.Attempts, "The first visit starts a heartbeat on X, which is held.");
            }

            Assert.AreEqual(BrokerFrameKind.Heartbeat, await ReadIncludingHeartbeatsAsync(healthy),
                $"Y heartbeats on interval {interval + 1} while X's write is held.");
        }

        Assert.AreEqual(1, held.Attempts, "X's first heartbeat is held, and no later heartbeat is started on X.");
        Assert.IsNotNull(blocked);
    }

    [TestMethod]
    public async Task CatchUp_BoundedReads_RepublishesPerCall()
    {
        var liveness = new Liveness();
        var calls = Enumerable.Range(0, 4).Select(_ => new TestGate()).ToArray();
        var observed = new System.Collections.Concurrent.ConcurrentQueue<(UsnJournalCursor Cursor, int Limit)>();
        var host = liveness.Host(readJournal: (_, since, maximumBufferReads) =>
        {
            var index = checked((int)((since.NextUsn - Tip.NextUsn) / 100));
            Assert.IsTrue(index >= 0 && index < calls.Length);
            observed.Enqueue((since, maximumBufferReads));
            calls[index].MarkEntered();
            calls[index].WaitForRelease();
            return index < 3
                ? ([JournalEntries.Create((ulong)since.NextUsn, since.NextUsn, "entry.txt")],
                    since with { NextUsn = since.NextUsn + 100 })
                : ([], since);
        });
        using var sectionWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, sectionWriter);
        try
        {
            var pipe = await harness.OpenScanChannelAsync('C');
            foreach (var expected in new[] { BrokerFrameKind.Cursor, BrokerFrameKind.ScanProgress, BrokerFrameKind.ScanReady })
            {
                Assert.AreEqual(expected, await ReadIncludingHeartbeatsAsync(pipe));
            }
            await calls[0].Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            await liveness.AdvanceOneIntervalAsync(); // Consume the operation-frame skip.
            foreach (var call in calls)
            {
                await call.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
                for (var interval = 0; interval < 4; interval++)
                {
                    await liveness.AdvanceOneIntervalAsync();
                    Assert.AreEqual(BrokerFrameKind.Heartbeat, await ReadIncludingHeartbeatsAsync(pipe));
                }
                call.Release();
            }
            var rest = await HostChannelHarness.ReadToEndAsync(pipe, includeHeartbeats: true);
            Assert.AreSequenceEqual([BrokerFrameKind.ScanCompleted], [.. rest.Select(frame => frame.Kind)]);
            Assert.IsEmpty(rest[0].Entries);
            Assert.AreEqual(default(UsnJournalCursor), rest[0].Cursor);
            Assert.AreSequenceEqual([.. Enumerable.Range(0, 4).Select(index => Tip with { NextUsn = Tip.NextUsn + (index * 100) })], [.. observed.Select(read => read.Cursor)]);
            Assert.AreSequenceEqual([256, 256, 256, 256], [.. observed.Select(read => read.Limit)]);
        }
        finally
        {
            foreach (var call in calls)
            {
                call.Release();
            }
        }
    }

    [TestMethod]
    public async Task CatchUp_SecondBoundedReadFails_WritesCatchUpLostAndNoBatch()
    {
        using var journal = JournalIsolation.OverrideJournalWindow(_ => new SyntheticJournalWindow(7, 5000, 9000, 4096, 32768));
        var liveness = new Liveness();
        var calls = 0;
        var host = liveness.Host(readJournal: (_, since, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                throw new IOException("second catch-up read failed");
            }

            return ([JournalEntries.Create(1, since.NextUsn, "first.txt")], since with { NextUsn = since.NextUsn + 100 });
        });
        var startTime = liveness.Clock.GetUtcNow();
        using var sectionWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, sectionWriter);

        var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'),
            includeHeartbeats: true);

        Assert.AreEqual(BrokerFrameKind.ScanReady, frames[^2].Kind);
        Assert.AreEqual(BrokerFrameKind.CatchUpLost, frames[^1].Kind, "CatchUpLost is the last frame, then EOF.");
        Assert.IsNull(frames[^1].Message, "The failure text stays on the host.");
        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.ScanCompleted, frames);
        Assert.DoesNotContain(frame => frame.Kind == BrokerFrameKind.JournalBatch, frames);
        Assert.IsNotNull(frames[^1].CatchUpLoss);
        Assert.AreEqual(2, calls, "A failed bounded read is not retried.");
        Assert.AreEqual(startTime, liveness.Clock.GetUtcNow(), "The host never waited on its clock.");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task BlockFlush_EachRange_RestartsScanPipeProgressClock()
    {
        var liveness = new Liveness();
        using var blockDirectory = new TempDirectory("mftlib-broker-block");
        var sectionName = NamedBlockSection.BuildSectionName('C');
        // Two whole flush ranges of name pool, so the view spans three ranges.
        var (block, lifetime) = NamedBlockSection.Create(
            TestBlocks.Options(Path.Combine(blockDirectory.Path, "block.bin"), ProducerKind.Mft, 128,
                (uint)(2 * BlockFile.FlushRangeBytes), volumeSerial: 123, rootRow: 5, deleteOnClose: true),
            sectionName);
        using (block)
        using (lifetime)
        {
            var ranges = (block.Length + BlockFile.FlushRangeBytes - 1) / BlockFile.FlushRangeBytes;
            Assert.AreEqual(3L, ranges);
            var flushes = Enumerable.Range(0, checked((int)ranges)).Select(_ => new TestGate()).ToArray();
            await using var harness = new HostChannelHarness(
                liveness.Host(scanDrive: (_, _, _, _, _, _) => [[new MftRecord(5, 5, new MftRecordFields(3), ".")]]),
                new GatedFlushWriter(flushes, liveness.Clock));
            try
            {
                var pipe = await harness.OpenScanChannelAsync('C', sectionName);
                Assert.AreEqual(BrokerFrameKind.Cursor, await ReadIncludingHeartbeatsAsync(pipe));
                await flushes[0].Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
                await liveness.AdvanceOneIntervalAsync(); // Cursor accounts for the first visit.
                foreach (var flush in flushes)
                {
                    await flush.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
                    for (var interval = 0; interval < 4; interval++)
                    {
                        await liveness.AdvanceOneIntervalAsync();
                        Assert.AreEqual(BrokerFrameKind.Heartbeat, await ReadIncludingHeartbeatsAsync(pipe));
                    }
                    flush.Release();
                }
                var frames = await HostChannelHarness.ReadToEndAsync(pipe, includeHeartbeats: true);
                Assert.AreSequenceEqual(
                [
                    BrokerFrameKind.ScanProgress, BrokerFrameKind.ScanReady, BrokerFrameKind.ScanCompleted
                ], [.. frames.Select(frame => frame.Kind)]);
                Assert.IsTrue(block.Header.IsComplete);
                GC.KeepAlive(block);
            }
            finally
            {
                foreach (var flush in flushes)
                {
                    flush.Release();
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    sealed class GatedFlushWriter(TestGate[] ranges, TimeProvider clock) : IBlockSectionWriter
    {
        readonly RealBlockSectionWriter _inner = new(clock);

        public BlockWriteResult Write(string sectionName, UsnJournalCursor cursor,
            IEnumerable<IReadOnlyList<MftRecord>> batches, MftRowSelection selection,
            BlockWriteReporting reporting, CancellationToken cancellationToken)
        {
            Assert.IsNotNull(reporting.Operation);
            // Deliberately omit intermediate progress frames: only operation progress
            // may keep this scenario alive while the real block is flushed.
            return _inner.Write(sectionName, cursor, batches, selection,
                new BlockWriteReporting(null, new GatedFlushReporter(reporting.Operation, ranges)),
                cancellationToken);
        }
    }

    sealed class GatedFlushReporter(ISourceOperationReporter inner, TestGate[] ranges) : ISourceOperationReporter
    {
        int _nextRange;

        public void WaitingOnVolume() => inner.WaitingOnVolume();

        public void Processing(string stepName)
        {
            inner.Processing(stepName);
            if (stepName != MftBlockScan.FlushStep)
            {
                return;
            }
            var index = Interlocked.Increment(ref _nextRange) - 1;
            Assert.IsLessThan(ranges.Length, index, "Unexpected additional flush range.");
            ranges[index].MarkEntered();
            ranges[index].WaitForRelease();
        }
    }

    // Holds every write to the named drive's pipes; other drives' pipes pass through.
    static HostChannelHarness HarnessHoldingWritesTo(char drive, JournalBrokerHost host, HeldWrites held)
    {
        return new HostChannelHarness(host, wrapDrivePipe: (pipeName, hostEnd) =>
            pipeName.StartsWith(HostChannelHarness.PipePrefix(drive), StringComparison.Ordinal) ? held.Wrap(hostEnd) : hostEnd);
    }
}
