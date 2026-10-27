using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class LocalMftCacheFreshnessTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(99UL, 1000L, 2001L, false, JournalCheckpointLossCause.JournalAdvanced)]
    [DataRow(100UL, 1000L, 2000L, false, JournalCheckpointLossCause.JournalRecreated)]
    [DataRow(99UL, 2001L, 3000L, false, JournalCheckpointLossCause.CheckpointTrimmed)]
    [DataRow(99UL, 1000L, 2001L, true, JournalCheckpointLossCause.JournalAdvanced)]
    public async Task Reopen_ChangedJournalRescansOrFlagsCacheOnlySnapshot(
        ulong journalIdentifier, long firstUsn, long nextUsn, bool cacheOnly, JournalCheckpointLossCause cause)
    {
        using var fixture = new IndexTreeFixture(seedDocuments: false);
        var scans = 0;
        var access = new ScriptedVolumeAccess
        {
            VolumeInformation = _ => new NtfsVolumeInformation(1024 * 1000, 1024),
            RecordBatches = (_, _, _, _, _, _) =>
            {
                Interlocked.Increment(ref scans);
                return [[new MftRecord(5, 5, new MftRecordFields(3), ".")]];
            },
            CursorQuery = _ => new UsnJournalCursor(99, 2000)
        };
        var source = new LocalMftBlockProducer(
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero)), null, access)
            .CreateIndexSource();
        var options = fixture.Options(drive: 'T', volumeSerial: 123, producer: ProducerKind.Mft) with
        {
            MftSource = source
        };
        await using (var initial = await FileIndex.OpenAsync(options, TestContext.CancellationToken))
        {
            Assert.AreEqual(DriveState.Ready, initial.Status.State);
        }
        using var journal = JournalIsolation.OverrideJournalWindow(
            _ => new SyntheticJournalWindow(journalIdentifier, firstUsn, nextUsn, 64, 32768));
        options = options with { CacheMode = cacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent };
        await using var reopened = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        var status = reopened.Status;
        Assert.AreEqual(cacheOnly ? 1 : 2, scans);
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(cacheOnly ? BlockOrigin.WarmStartedFromCache : BlockOrigin.ProducedByScan, status.Block!.Origin);
        Assert.AreEqual(cause, status.Watch.CheckpointLoss?.Cause);
        Assert.IsFalse(status.Watch.IsSupported);
        Assert.IsFalse(status.Watch.IsRequested);
    }
}
