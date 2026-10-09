using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public class LocalMftBlockProducerCursorTests
{
    public TestContext TestContext { get; set; } = null!;
    OwnedIndexDirectories _directories = null!;

    [TestInitialize]
    public void Initialize() => _directories = new OwnedIndexDirectories();

    [TestCleanup]
    public void Cleanup() => _directories.Dispose();

    [TestMethod]
    public async Task CachedScan_ArmsCursorBeforeBatchesAndStampsResult()
    {
        var directories = _directories;
        Directory.CreateDirectory(directories.CacheDirectory);
        var calls = new List<string>();
        var cursor = new UsnJournalCursor(99, 2000);
        var seams = new LocalMftBlockProducer.Seams(
            _ => new NtfsVolumeInformation(1024 * 1000, 1024),
            (_, _, _, _, _, _) =>
            {
                calls.Add("scan");
                return [[new MftRecord(5, 5, new MftRecordFields(3), ".")]];
            }, () => new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc),
            _ =>
            {
                calls.Add("cursor");
                return cursor;
            });
        var result = await new LocalMftBlockProducer(null, seams).CreateIndexSource().Producer(new MftBlockProduceRequest
        {
            DriveLetter = 'T',
            VolumeSerial = 123,
            BlockPath = Path.Combine(directories.CacheDirectory, "cached.mlix")
        }, TestContext.CancellationTokenSource.Token);
        using var block = result.Block;
        CollectionAssert.AreEqual(new[] { "cursor", "scan" }, calls);
        Assert.AreEqual(cursor.JournalIdentifier, result.JournalIdentifier);
        Assert.AreEqual(cursor.NextUsn, result.NextUsn);
        Assert.AreEqual(cursor.JournalIdentifier, block.Header.UsnJournalId);
        Assert.AreEqual(cursor.NextUsn, block.Header.UsnNextUsn);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task JournalLessVolume_FailsCachedDriveButSkipsQueryWithoutCache(bool noCache)
    {
        var directories = _directories;
        Directory.CreateDirectory(directories.TreeRoot);
        Directory.CreateDirectory(directories.CacheDirectory);
        var scans = 0;
        var queries = 0;
        var source = new LocalMftBlockProducer(null, new LocalMftBlockProducer.Seams(
            _ => new NtfsVolumeInformation(1024 * 1000, 1024),
            (_, _, _, _, _, _) =>
            {
                scans++;
                return [[new MftRecord(5, 5, new MftRecordFields(3), ".")]];
            }, () => new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc),
            _ =>
            {
                queries++;
                throw new InvalidOperationException("No active USN journal.");
            })).CreateIndexSource();
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', directories.TreeRoot, 123)],
            CacheDirectory = directories.CacheDirectory,
            NoCache = noCache,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = source
        }, TestContext.CancellationTokenSource.Token);
        var status = index.Drives.Single();
        Assert.AreEqual(noCache ? 0 : 1, queries);
        Assert.AreEqual(noCache ? 1 : 0, scans);
        Assert.AreEqual(noCache ? DriveState.Ready : DriveState.Failed, status.State);
        if (!noCache)
        {
            Assert.AreEqual(DriveFailureKind.ProducerFailed, status.FailureKind);
            Assert.AreEqual(0, Directory.GetFiles(directories.CacheDirectory, "*.mlix").Length);
        }
    }
}
