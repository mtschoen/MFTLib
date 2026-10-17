using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>An enumeration scan of a tree larger than its first capacity plan plans a larger block on rescan.</summary>
[TestClass]
public class FileIndexEnumerationGrowthTests
{
    /// <summary>
    ///     The root holds one directory, so the first plan is the 4096-row minimum, whose name pool of about 1.2
    ///     MiB cannot hold 5000 names of 400 bytes each, while its row capacity easily holds them.
    /// </summary>
    const int FileCount = 5000;

    OwnedIndexDirectories _directories = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directories = new OwnedIndexDirectories();
        var crowded = Path.Combine(_directories.TreeRoot, "crowded");
        Directory.CreateDirectory(crowded);
        var padding = new string('n', 200);
        Parallel.For(0, FileCount, number => File.WriteAllBytes(Path.Combine(crowded, $"{padding}{number}"), []));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _directories.Dispose();
    }

    [TestMethod]
    public async Task Rescan_ATreeBeyondTheFirstCapacityPlanProducesACompleteBlock()
    {
        var options = new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _directories.TreeRoot, 0x0BADF00D, ProducerKind.Enumeration)],
            CacheDirectory = _directories.CacheDirectory
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.IsTrue(index.Drives.Single().Block!.IsCapacityExhausted);

        await index.RescanAsync(['T'], CancellationToken.None);
        Assert.IsFalse(index.Drives.Single().Block!.IsCapacityExhausted);
        Assert.AreEqual((uint)FileCount + 2, index.Drives.Single().Block!.LiveRowCount);
    }
}
