using MFTLib.Index;
using MFTLib.Tests.TestSupport;
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
        ulong journalId, long firstUsn, long nextUsn, bool cacheOnly, JournalCheckpointLossCause cause)
    {
        var directories = new OwnedIndexDirectories();
        try
        {
            Directory.CreateDirectory(directories.TreeRoot);
            Directory.CreateDirectory(directories.CacheDirectory);
            var scans = 0;
            var source = new LocalMftBlockProducer(null, new LocalMftBlockProducer.Seams(
                _ => new NtfsVolumeInformation(1024 * 1000, 1024),
                (_, _, _, _, _, _) =>
                {
                    Interlocked.Increment(ref scans);
                    return [[new MftRecord(5, 5, new MftRecordFields(3), ".")]];
                }, () => new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc),
                _ => new UsnJournalCursor(99, 2000))).CreateIndexSource();
            var options = new FileIndexOptions
            {
                Drives = [new IndexedDrive('T', directories.TreeRoot, 123)],
                CacheDirectory = directories.CacheDirectory,
                ProducerPolicy = ProducerPolicy.Mft,
                MftSource = source
            };
            await using (var initial = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token))
            {
                Assert.AreEqual(DriveState.Ready, initial.Drives.Single().State);
            }
            using var journal = JournalCheckpointCheck.OverrideJournalForTest(
                _ => new JournalWindow(journalId, firstUsn, nextUsn, 64, 32768));
            options = options with { InitialOpenCacheOnly = cacheOnly };
            await using var reopened = await FileIndex.OpenAsync(options, TestContext.CancellationTokenSource.Token);
            var status = reopened.Drives.Single();
            Assert.AreEqual(cacheOnly ? 1 : 2, scans);
            Assert.AreEqual(DriveState.Ready, status.State);
            Assert.AreEqual(cacheOnly ? BlockSource.WarmStartedFromCache : BlockSource.ProducedByScan, status.Block.Source);
            Assert.AreEqual(cause, status.Watch.CheckpointLoss?.Cause);
            Assert.IsFalse(status.Watch.Supported);
            Assert.IsFalse(status.Watch.Requested);
            if (cacheOnly)
            {
                var field = typeof(FileIndex).GetField("_unresumableCheckpointsByOrdinal",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var unresumable = (System.Collections.IDictionary)field.GetValue(reopened)!;
                Assert.AreEqual(1, unresumable.Count);
            }
        }
        finally
        {
            directories.Dispose();
        }
    }
}
