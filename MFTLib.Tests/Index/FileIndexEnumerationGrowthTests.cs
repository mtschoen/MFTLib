using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>An enumeration scan of a tree larger than its first capacity plan plans a larger block on rescan.</summary>
[TestClass]
public sealed class FileIndexEnumerationGrowthTests : IDisposable
{
    /// <summary>
    ///     The root holds one directory, so the first plan is the 4096-row minimum, whose name pool of about 1.2
    ///     MiB cannot hold 5000 names of 400 bytes each, while its row capacity easily holds them.
    /// </summary>
    const int FileCount = 5000;

    IndexTreeFixture _fixture = null!;
    string _treeRoot => _fixture.TreeRoot;

    [TestInitialize]
    public void Initialize()
    {
        _fixture = new IndexTreeFixture(seedDocuments: false);
        var crowded = Path.Combine(_treeRoot, "crowded");
        Directory.CreateDirectory(crowded);
        var padding = new string('n', 200);
        Parallel.For(0, FileCount, number => File.WriteAllBytes(Path.Combine(crowded, $"{padding}{number}"), []));
    }

    public void Dispose() => _fixture.Dispose();

    [TestMethod]
    public async Task Rescan_ATreeBeyondTheFirstCapacityPlanProducesACompleteBlock()
    {
        var options = _fixture.Options();

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.IsTrue(index.Drives.Single().Block!.IsCapacityExhausted);

        await index.RescanAsync(['T'], CancellationToken.None);
        Assert.IsFalse(index.Drives.Single().Block!.IsCapacityExhausted);
        Assert.AreEqual((uint)FileCount + 2, index.Drives.Single().Block!.LiveRowCount);
    }
}
