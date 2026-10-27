using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LocalMftBlockProducerCursorTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    [TestMethod]
    public async Task CachedScan_ArmsCursorBeforeBatchesAndStampsResult()
    {
        Directory.CreateDirectory(CacheDirectoryPath);
        var calls = new List<string>();
        var cursor = new UsnJournalCursor(99, 2000);
        var access = new ScriptedVolumeAccess
        {
            VolumeInformation = _ => new NtfsVolumeInformation(1024 * 1000, 1024),
            RecordBatches = (_, _, _, _, _, _) =>
            {
                calls.Add("scan");
                return [[new MftRecord(5, 5, new MftRecordFields(3), ".")]];
            },
            CursorQuery = _ =>
            {
                calls.Add("cursor");
                return cursor;
            }
        };
        var result = await new LocalMftBlockProducer(
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero)), null, access)
            .CreateIndexSource().Producer(new MftBlockProduceRequest
            {
                Drive = 'T',
                VolumeSerial = 123,
                BlockPath = Path.Combine(CacheDirectoryPath, "cached.mlix")
            }, TestContext.CancellationToken);
        using var block = result.Block;
        Assert.AreSequenceEqual(["cursor", "scan"], calls);
        Assert.AreEqual(cursor.JournalIdentifier, block.Header.UsnJournalIdentifier);
        Assert.AreEqual(cursor.NextUsn, block.Header.UsnNextUsn);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task JournalLessVolume_FailsCachedDriveButSkipsQueryWithoutCache(bool cacheDisabled)
    {
        Directory.CreateDirectory(CacheDirectoryPath);
        var scans = 0;
        var queries = 0;
        var access = new ScriptedVolumeAccess
        {
            VolumeInformation = _ => new NtfsVolumeInformation(1024 * 1000, 1024),
            RecordBatches = (_, _, _, _, _, _) =>
            {
                scans++;
                return [[new MftRecord(5, 5, new MftRecordFields(3), ".")]];
            },
            CursorQuery = _ =>
            {
                queries++;
                throw new InvalidOperationException("No active USN journal.");
            }
        };
        var source = new LocalMftBlockProducer(
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero)), null, access)
            .CreateIndexSource();
        await using var index = await FileIndex.OpenAsync(Fixture.Options(
            drive: 'T',
            volumeSerial: 123,
            producer: ProducerKind.Mft,
            cacheMode: cacheDisabled ? CacheMode.Disabled : CacheMode.Persistent) with
        {
            MftSource = source
        }, TestContext.CancellationToken);
        var status = index.Drives.Single();
        Assert.AreEqual(cacheDisabled ? 0 : 1, queries);
        Assert.AreEqual(cacheDisabled ? 1 : 0, scans);
        Assert.AreEqual(cacheDisabled ? DriveState.Ready : DriveState.Failed, status.State);
        if (!cacheDisabled)
        {
            Assert.AreEqual(DriveFailureKind.ProducerFailed, status.Failure?.Kind);
            Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath, "*.mlix"));
        }
    }
}
