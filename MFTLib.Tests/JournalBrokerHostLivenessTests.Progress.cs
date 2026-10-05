using System.Runtime.Versioning;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
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
        await using var harness = HarnessHoldingWritesTo('X', liveness.Host(watchDrive: NeverYields), held);
        var blocked = await harness.OpenWatchChannelAsync('X', BehindTip);
        var healthy = await harness.OpenWatchChannelAsync('Y', BehindTip);
        await liveness.WhenPublished(DriveTag('X', 1), ChannelOperationKind.WaitingOnVolume);
        await liveness.WhenPublished(DriveTag('Y', 1), ChannelOperationKind.WaitingOnVolume);

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
        var reads = new List<int>();
        var host = liveness.Host(readJournal: (_, since, maximumBufferReads) =>
        {
            reads.Add(maximumBufferReads);
            // Three chunks of one entry each, 100 USNs apart, then the tip.
            return since.NextUsn < Tip.NextUsn + 300
                ? ([JournalEntries.Create((ulong)since.NextUsn, since.NextUsn, $"{since.NextUsn}.txt")],
                    since with { NextUsn = since.NextUsn + 100 })
                : ([], since);
        });
        using var sectionWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, sectionWriter);

        var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C'));

        var terminal = frames[^1];
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, terminal.Kind);
        Assert.AreEqual(0, terminal.Entries.Length, "The chunks the catch-up read stay on the host.");
        Assert.AreEqual(Tip with { NextUsn = 1300 }, terminal.Cursor, "The terminal frame carries the cursor after every chunk.");
        CollectionAssert.AreEqual(Enumerable.Repeat(BrokerLiveness.CatchUpBufferReadsPerCall, 4).ToArray(),
            reads.ToArray(), "Each call is bounded; the fourth returns at the tip and ends catch-up.");
        Assert.AreEqual(3, liveness.Republishes(DriveTag('C', 1), "journal catch-up"),
            "The progress clock restarts once per call that returned a chunk.");
    }

    [TestMethod]
    public async Task CatchUp_SecondBoundedReadFails_WritesCatchUpLostAndNoBatch()
    {
        using var journal = JournalCheckpointCheck.OverrideJournalForTest(_ => new JournalWindow(7, 5000, 9000, 4096, 32768));
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
        Assert.IsFalse(frames.Any(frame => frame.Kind == BrokerFrameKind.ScanCompleted));
        Assert.AreEqual(2, calls, "A failed bounded read is not retried.");
        Assert.AreEqual(1, liveness.Republishes(DriveTag('C', 1), "journal catch-up"),
            "Only the call that returned a chunk restarted the progress clock.");
        Assert.AreEqual(startTime, liveness.Clock.GetUtcNow(), "The host never waited on its clock.");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task BlockFlush_EachRange_RestartsScanPipeProgressClock()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Named block sections require Windows.");
        }

        var liveness = new Liveness();
        var sectionName = NamedBlockSection.BuildSectionName('C');
        var (block, lifetime) = NamedBlockSection.Create(new BlockFileCreateOptions
        {
            Path = Path.Combine(Path.GetTempPath(), $"broker-flush-{Guid.NewGuid():N}.bin"),
            VolumeSerial = 123,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = 128,
            // Two whole flush ranges of name pool, so the view spans three ranges.
            NamePoolCapacity = (uint)(2 * BlockFile.FlushRangeBytes),
            DeleteOnClose = true
        }, sectionName);
        using (block)
        using (lifetime)
        {
            var ranges = (block.Length + BlockFile.FlushRangeBytes - 1) / BlockFile.FlushRangeBytes;
            Assert.AreEqual(3L, ranges);
            await using var harness = new HostChannelHarness(
                liveness.Host(scanDrive: (_, _, _, _, _) => [[new MftRecord(5, 5, new MftRecordFields(3), ".", null)]]),
                new RealBlockSectionWriter());

            var frames = await HostChannelHarness.ReadToEndAsync(await harness.OpenScanChannelAsync('C', sectionName));

            Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
            Assert.AreEqual(ranges, liveness.Published.Count(entry =>
                entry.Tag == DriveTag('C', 1) && entry.State is
                { Kind: ChannelOperationKind.Processing, Step: RealBlockSectionWriter.FlushStep }),
                "Each flushed range republishes the scan pipe's state.");
        }
    }

    // Holds every write to the named drive's pipes; other drives' pipes pass through.
    static HostChannelHarness HarnessHoldingWritesTo(char drive, JournalBrokerHost host, HeldWrites held)
    {
        return new HostChannelHarness(host, wrapDrivePipe: (pipeName, hostEnd) =>
            pipeName.StartsWith($"harness-{drive}-", StringComparison.Ordinal) ? held.Wrap(hostEnd) : hostEnd);
    }
}
