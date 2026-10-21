using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>An enumeration scan grows its block until the whole tree fits.</summary>
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
    string _treeRoot => _fixture.TreeRoot;

    [TestInitialize]
    public void Initialize()
    {
        _fixture = new IndexTreeFixture(seedDocuments: false);
        var crowded1 = Path.Combine(_treeRoot, "crowded1");
        var crowded2 = Path.Combine(_treeRoot, "crowded2");
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
    public async Task Open_ATreeBeyondTheFirstCapacityPlanProducesACompleteBlock()
    {
        var observation = new EnumerationReadObservation();
        EnumerationWalkLimit.Hooks.DirectoryEntered = observation.Enter;
        EnumerationWalkLimit.Hooks.DirectoryExited = observation.Exit;
        try
        {
            var retryObserved = false;
            var diagnostics = new List<string>();
            var options = _fixture.Options() with
            {
                Diagnostics = line =>
                {
                    diagnostics.Add(line);
                    if (line.Contains("capacity exhausted", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.AreEqual(0, observation.Active);
                        retryObserved = true;
                    }
                }
            };

            await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
            Assert.IsTrue(retryObserved);
            Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
            Assert.IsFalse(index.Drives.Single().Block!.IsCapacityExhausted);
            Assert.AreEqual((uint)FileCount + 3, index.Drives.Single().Block!.LiveRowCount);
            var expectedFiles = Directory.GetFiles(Path.Combine(_treeRoot, "crowded1"))
                .Concat(Directory.GetFiles(Path.Combine(_treeRoot, "crowded2")))
                .ToArray();
            CollectionAssert.AreEquivalent(
                expectedFiles,
                index.Search(new SearchQuery(null, IsDirectory: false)).Select(entry => entry.Path).ToArray());
            Assert.IsTrue(diagnostics.Any(line => line.Contains(_fixture.CacheDirectory) &&
                                                  line.Contains("capacity", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            EnumerationWalkLimit.Hooks.ResetToDefaults();
        }
    }
}
