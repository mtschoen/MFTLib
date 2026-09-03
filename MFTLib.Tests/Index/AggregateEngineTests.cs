using System.Diagnostics.CodeAnalysis;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                     "throughout rather than IDisposable on the test class itself.")]
public class AggregateEngineTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    SyntheticBlockBuilder _builder = null!;
    Snapshot _snapshot = null!;
    uint _documentsRow;

    [TestInitialize]
    public void Initialize()
    {
        _builder = new SyntheticBlockBuilder(slotCapacity: 512, namePoolCapacity: 8192);
        var root = _builder.AddRoot();
        _documentsRow = _builder.AddRow("Documents", root, RowFlags.InUse | RowFlags.Directory, 0, Moment);
        var pictures = _builder.AddRow("Pictures", root, RowFlags.InUse | RowFlags.Directory, 0, Moment);
        _builder.AddRow("huge.bin", _documentsRow, RowFlags.InUse, 9_000_000, Moment);
        _builder.AddRow("large.bin", _documentsRow, RowFlags.InUse, 5_000_000, Moment);
        _builder.AddRow("medium.bin", pictures, RowFlags.InUse, 3_000_000, Moment);
        _builder.AddRow("small.bin", pictures, RowFlags.InUse, 1_000, Moment);
        _builder.AddRow("readme.md", _documentsRow, RowFlags.InUse, 10, Moment);
        _builder.AddRow("readme.md", pictures, RowFlags.InUse, 20, Moment);
        _builder.AddRow("gone.bin", _documentsRow, RowFlags.InUse | RowFlags.Tombstone, 99_000_000, Moment);
        _builder.Complete(Moment);

        var block = _builder.OpenForReading(out _)!;
        _snapshot = Snapshot.Create([new DriveBlock('T', 0, block, deleteFileOnRelease: false)]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _snapshot.ReleaseNow();
        _builder.Dispose();
    }

    [TestMethod]
    public void Largest_ReturnsTheBiggestFilesInDescendingOrder()
    {
        var results = AggregateEngineTestAccess.Largest(_snapshot, 3, under: null);
        CollectionAssert.AreEqual(
            new[] { "huge.bin", "large.bin", "medium.bin" },
            results.Select(entry => entry.Name).ToArray());
    }

    [TestMethod]
    public void Largest_ExcludesDirectoriesAndTombstones()
    {
        var results = AggregateEngineTestAccess.Largest(_snapshot, 10, under: null);
        Assert.IsFalse(results.Any(entry => entry.IsDirectory));
        Assert.IsFalse(results.Any(entry => entry.Name == "gone.bin"));
    }

    [TestMethod]
    public void Largest_ExcludesSizeUnknownRows()
    {
        using var builder = new SyntheticBlockBuilder('K');
        var root = builder.AddRoot();
        builder.AddRow("known-small.txt", root, RowFlags.InUse, 10, Moment);
        builder.AddRow("unknown-size.txt", root, RowFlags.InUse | RowFlags.SizeUnknown, 0, Moment);
        builder.AddRow("known-big.txt", root, RowFlags.InUse, 1000, Moment);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('K', 0, block, deleteFileOnRelease: false)]);
        try
        {
            var results = AggregateEngineTestAccess.Largest(snapshot, 10, under: null);
            CollectionAssert.AreEqual(
                new[] { "known-big.txt", "known-small.txt" },
                results.Select(entry => entry.Name).ToArray());
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Largest_RespectsTheSubtreeRestriction()
    {
        var documents = FileEntry.Create(_snapshot, 0, _documentsRow);
        var results = AggregateEngineTestAccess.Largest(_snapshot, 5, documents);
        CollectionAssert.AreEqual(
            new[] { "huge.bin", "large.bin", "readme.md" },
            results.Select(entry => entry.Name).ToArray());
    }

    [TestMethod]
    public void Largest_CountLargerThanTheDriveReturnsEverything()
    {
        var results = AggregateEngineTestAccess.Largest(_snapshot, 1000, under: null);
        Assert.AreEqual(6, results.Count);
    }

    [TestMethod]
    public void Largest_ZeroCountReturnsEmpty()
    {
        Assert.AreEqual(0, AggregateEngineTestAccess.Largest(_snapshot, 0, under: null).Count);
    }

    [TestMethod]
    public void Largest_NegativeCountThrows()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => AggregateEngineTestAccess.Largest(_snapshot, -1, under: null));
    }

    [TestMethod]
    public void DuplicateNames_GroupsRowsThatShareAName()
    {
        var groups = AggregateEngineTestAccess.DuplicateNames(_snapshot);
        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual("readme.md", groups[0].Name);
        Assert.AreEqual(2, groups[0].Entries.Count);
    }

    [TestMethod]
    public void DuplicateNames_IgnoresTombstonesAndSingletons()
    {
        var groups = AggregateEngineTestAccess.DuplicateNames(_snapshot);
        Assert.IsFalse(groups.Any(group => group.Name == "huge.bin"));
        Assert.IsFalse(groups.Any(group => group.Name == "gone.bin"));
    }

    [TestMethod]
    public void DuplicateNames_FoldsCaseTheWayNtfsDoes()
    {
        using var builder = new SyntheticBlockBuilder('V');
        var root = builder.AddRoot();
        var first = builder.AddRow("Sub", root, RowFlags.InUse | RowFlags.Directory, 0, Moment);
        builder.AddRow("Notes.TXT", root, RowFlags.InUse, 1, Moment);
        builder.AddRow("notes.txt", first, RowFlags.InUse, 2, Moment);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('V', 0, block, deleteFileOnRelease: false)]);
        try
        {
            var groups = AggregateEngineTestAccess.DuplicateNames(snapshot);
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual(2, groups[0].Entries.Count);
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    // 100,000 rows rather than the 21 million a real drive might carry: enough to exercise the
    // single-pass scan and the two-pass hash table at a scale no per-row list could ignore, while
    // staying well under the "roughly two seconds" test budget.
    const int LargeRowCount = 100_000;

    [TestMethod]
    public void Largest_OverALargeBlockReturnsTheExactTopFiveInDescendingOrder()
    {
        using var builder = new SyntheticBlockBuilder('L',
            slotCapacity: LargeRowCount + 10, namePoolCapacity: (uint)LargeRowCount * 40);
        var root = builder.AddRoot();
        for (var size = 1; size <= LargeRowCount; size++)
        {
            builder.AddRow($"file{size}.bin", root, RowFlags.InUse, size, Moment);
        }

        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('L', 0, block, deleteFileOnRelease: false)]);
        try
        {
            var results = AggregateEngineTestAccess.Largest(snapshot, 5, under: null);
            CollectionAssert.AreEqual(
                Enumerable.Range(0, 5).Select(offset => $"file{LargeRowCount - offset}.bin").ToArray(),
                results.Select(entry => entry.Name).ToArray());
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void DuplicateNames_OverALargeTwoDriveBlockFindsExactlyTheSharedNames()
    {
        var uniqueRowCountOnDriveA = LargeRowCount - 3;
        using var builderA = new SyntheticBlockBuilder('A',
            slotCapacity: (uint)uniqueRowCountOnDriveA + 10, namePoolCapacity: (uint)uniqueRowCountOnDriveA * 40);
        var rootA = builderA.AddRoot();
        for (var index = 0; index < uniqueRowCountOnDriveA; index++)
        {
            builderA.AddRow($"a-only-{index}.bin", rootA, RowFlags.InUse, index, Moment);
        }

        builderA.AddRow("shared1.bin", rootA, RowFlags.InUse, 1, Moment);
        builderA.AddRow("shared2.bin", rootA, RowFlags.InUse, 2, Moment);
        builderA.AddRow("shared3.bin", rootA, RowFlags.InUse, 3, Moment);
        builderA.Complete(Moment);

        using var builderB = new SyntheticBlockBuilder('B');
        var rootB = builderB.AddRoot();
        builderB.AddRow("shared1.bin", rootB, RowFlags.InUse, 10, Moment);
        builderB.AddRow("shared2.bin", rootB, RowFlags.InUse, 20, Moment);
        builderB.AddRow("shared3.bin", rootB, RowFlags.InUse, 30, Moment);
        builderB.AddRow("b-only.bin", rootB, RowFlags.InUse, 40, Moment);
        builderB.Complete(Moment);

        var blockA = builderA.OpenForReading(out _)!;
        var blockB = builderB.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([
            new DriveBlock('A', 0, blockA, deleteFileOnRelease: false),
            new DriveBlock('B', 1, blockB, deleteFileOnRelease: false)
        ]);
        try
        {
            var groups = AggregateEngineTestAccess.DuplicateNames(snapshot);
            CollectionAssert.AreEquivalent(
                new[] { "shared1.bin", "shared2.bin", "shared3.bin" },
                groups.Select(group => group.Name).ToArray());
            Assert.IsTrue(groups.All(group => group.Entries.Count == 2));
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }
}

static class AggregateEngineTestAccess
{
    public static List<FileEntry> Largest(Snapshot snapshot, int count, FileEntry? under)
    {
        return AggregateEngine.Largest(snapshot, count, under);
    }

    public static List<DuplicateGroup> DuplicateNames(Snapshot snapshot)
    {
        return AggregateEngine.DuplicateNames(snapshot);
    }
}
