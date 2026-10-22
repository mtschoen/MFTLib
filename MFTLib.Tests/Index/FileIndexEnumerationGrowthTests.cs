using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>An enumeration scan grows its block while the walk continues, until the whole tree fits.</summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexEnumerationGrowthTests : IDisposable
{
    /// <summary>
    ///     The root holds sibling directories, so the first plan is the 4096-row minimum, whose name pool of about 1.2
    ///     MiB cannot hold 5000 names of 400 bytes each, while its row capacity easily holds them.
    /// </summary>
    const int FileCount = 5000;

    IndexTreeFixture _fixture = null!;
    string TreeRoot => _fixture.TreeRoot;

    [TestInitialize]
    public void Initialize()
    {
        _fixture = new IndexTreeFixture(seedDocuments: false);
        var crowded1 = Path.Combine(TreeRoot, "crowded1");
        var crowded2 = Path.Combine(TreeRoot, "crowded2");
        Directory.CreateDirectory(crowded1);
        Directory.CreateDirectory(crowded2);
        var padding = new string('n', 200);
        Parallel.For(0, FileCount, number =>
        {
            var targetDir = number % 2 == 0 ? crowded1 : crowded2;
            File.WriteAllBytes(Path.Combine(targetDir, $"{padding}{number}"), []);
        });
    }

    public void Dispose() => _fixture.Dispose();

    [TestMethod]
    public async Task Open_ATreeBeyondTheFirstCapacityPlanProducesACompleteBlockReadingEachDirectoryOnce()
    {
        var observation = new EnumerationReadObservation();
        EnumerationWalkLimit.Hooks._directoryEntered = observation.Enter;
        EnumerationWalkLimit.Hooks._directoryExited = observation.Exit;
        try
        {
            var diagnostics = new List<string>();
            var options = _fixture.Options() with { Diagnostics = diagnostics.Add };

            await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
            Assert.Contains(line => line.Contains("capacity exhausted", StringComparison.OrdinalIgnoreCase), diagnostics);
            Assert.IsTrue(observation.Visits.Values.All(visits => visits == 1),
                "Growing the block must not read any directory a second time.");
            Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
            Assert.IsFalse(index.Drives.Single().Block!.IsCapacityExhausted);
            Assert.AreEqual((uint)FileCount + 3, index.Drives.Single().Block!.LiveRowCount);
            var expectedFiles = Directory.GetFiles(Path.Combine(TreeRoot, "crowded1"))
                .Concat(Directory.GetFiles(Path.Combine(TreeRoot, "crowded2")))
                .ToArray();
            Assert.AreSequenceEqual(
                expectedFiles, [.. index.Search(new SearchQuery(null, IsDirectory: false), TestContext.CancellationToken).Select(entry => entry.Path)], SequenceOrder.InAnyOrder);
            Assert.Contains(line => line.Contains(_fixture.CacheDirectory) &&
                                                  line.Contains("capacity", StringComparison.OrdinalIgnoreCase), diagnostics);
        }
        finally
        {
            EnumerationWalkLimit.Hooks.ResetToDefaults();
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
