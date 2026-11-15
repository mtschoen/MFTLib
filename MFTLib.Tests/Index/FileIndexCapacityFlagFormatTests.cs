using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.WatchGrowthFixture;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953, format version 10: the capacity-exhausted flag now means a change could not be stored because
///     growth could not make room. A version 9 block, whose flag may stand for changes dropped behind its cursor, is
///     rejected and cold-scans once; a flagged version 10 block warm-opens and watches normally.
/// </summary>
[TestClass]
public sealed class FileIndexCapacityFlagFormatTests : IndexTreeTestBase
{
    const int FormatVersionOffset = 4;
    const int FlagsOffset = 12;

    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    Task<WatchGrowthFixture> OpenAsync() =>
        WatchGrowthFixture.OpenAsync(CacheDirectoryPath, TreeRoot, UniqueVolumeSerial);

    /// <summary>Seeds the canonical block with a cold scan, then rewrites its format version and flags on disk.</summary>
    async Task SeedBlockAsync(uint formatVersion, BlockFlags flags)
    {
        string path;
        await using (var seed = await OpenAsync())
        {
            path = seed.CanonicalPath;
        }

        var bytes = await File.ReadAllBytesAsync(path, TestContext.CancellationToken);
        BitConverter.GetBytes(formatVersion).CopyTo(bytes, FormatVersionOffset);
        BitConverter.GetBytes((uint)flags).CopyTo(bytes, FlagsOffset);
        await File.WriteAllBytesAsync(path, bytes, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task AVersionNineBlock_IsRejectedAndColdScansOnce()
    {
        await SeedBlockAsync(9, BlockFlags.Complete | BlockFlags.CapacityExhausted);

        await using (var rejected = await OpenAsync())
        {
            Assert.Contains(line => line.Contains("cache validation failed: WrongFormatVersion"), rejected.DiagnosticsLines);
            Assert.AreEqual(BlockOrigin.ProducedByScan, rejected.Index.Status.Block!.Origin);
            Assert.IsFalse(rejected.Index.Status.Block.IsCapacityExhausted);
        }

        await using var reopened = await OpenAsync();
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Index.Status.Block!.Origin, "the cold scan replaced it");
    }

    [TestMethod]
    public async Task AFlaggedVersionTenBlock_WarmOpensAndWatchesWithNoEvent()
    {
        await SeedBlockAsync(BlockLayout.FormatVersion, BlockFlags.Complete | BlockFlags.CapacityExhausted);

        await using var fixture = await OpenAsync();
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, fixture.Index.Status.Block!.Origin);
        Assert.IsTrue(fixture.Index.Status.Block.IsCapacityExhausted);
        var watch = await fixture.StartWatchingAsync();
        await watch.PublishBatchAsync([CreateRecord(10, "stored.txt", 4100)], new SyntheticJournalCursor(JournalIdentifier, 5000));

        Assert.AreEqual("stored.txt", fixture.Changes.Single().Entry.Name);
        Assert.IsEmpty(fixture.Faults);
        Assert.AreEqual(RootOperationOutcome.Succeeded,
            (await fixture.Index.StopWatchingAsync(TestContext.CancellationToken)).Outcome);
    }
}
