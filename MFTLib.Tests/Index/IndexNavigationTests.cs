using System.Diagnostics.CodeAnalysis;
using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                     "throughout rather than IDisposable on the test class itself.")]
public class IndexNavigationTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    SyntheticBlockBuilder _builder = null!;
    Snapshot _snapshot = null!;
    uint _rootRow;
    uint _documentsRow;
    uint _projectsRow;
    uint _reportRow;
    uint _notesRow;

    [TestInitialize]
    public void Initialize()
    {
        _builder = new SyntheticBlockBuilder();
        _rootRow = _builder.AddRoot();
        _documentsRow = _builder.AddRow("Documents", _rootRow, RowFlags.InUse | RowFlags.Directory, 0, Moment);
        _projectsRow = _builder.AddRow("Projects", _documentsRow, RowFlags.InUse | RowFlags.Directory, 0, Moment);
        _reportRow = _builder.AddRow("report.pdf", _projectsRow, RowFlags.InUse, 4096, Moment);
        _notesRow = _builder.AddRow("notes.txt", _documentsRow, RowFlags.InUse, 128, Moment);
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

    FileEntry Entry(uint rowIndex)
    {
        return FileEntry.Create(_snapshot, 0, rowIndex);
    }

    [TestMethod]
    public void Path_JoinsNamesFromTheRootDown()
    {
        Assert.AreEqual(@"T:\Documents\Projects\report.pdf", Entry(_reportRow).Path);
        Assert.AreEqual(@"T:\Documents\notes.txt", Entry(_notesRow).Path);
    }

    [TestMethod]
    public void Path_OfTheRootIsTheDriveRoot()
    {
        Assert.AreEqual(@"T:\", Entry(_rootRow).Path);
    }

    [TestMethod]
    public void Parent_WalksOneLevelUpAndStopsAtTheRoot()
    {
        var report = Entry(_reportRow);
        var projects = report.Parent;
        Assert.IsTrue(projects.HasValue);
        Assert.AreEqual("Projects", projects.Value.Name);

        var root = Entry(_rootRow);
        Assert.IsNull(root.Parent);
    }

    [TestMethod]
    public void Children_ReturnsDirectChildrenOnly()
    {
        var children = Entry(_documentsRow).Children();
        var names = children.Select(child => child.Name).OrderBy(name => name).ToArray();

        CollectionAssert.AreEqual(new[] { "notes.txt", "Projects" }, names);
    }

    [TestMethod]
    public void Children_OfAFileIsEmpty()
    {
        Assert.AreEqual(0, Entry(_reportRow).Children().Count);
    }

    [TestMethod]
    public void Children_DoesNotReturnTheRootAsItsOwnChild()
    {
        var children = Entry(_rootRow).Children();
        Assert.IsFalse(children.Any(child => child.RowIndexForTest() == _rootRow));
        Assert.AreEqual(1, children.Count);
    }

    [TestMethod]
    public void IsUnder_MatchesTransitiveContainmentAndExcludesSiblings()
    {
        Assert.IsTrue(IndexNavigationTestAccess.IsUnder(Entry(_reportRow), Entry(_documentsRow)));
        Assert.IsTrue(IndexNavigationTestAccess.IsUnder(Entry(_reportRow), Entry(_projectsRow)));
        Assert.IsFalse(IndexNavigationTestAccess.IsUnder(Entry(_notesRow), Entry(_projectsRow)));
        Assert.IsTrue(IndexNavigationTestAccess.IsUnder(Entry(_projectsRow), Entry(_projectsRow)));
    }

    [TestMethod]
    public void Path_OfATombstonedFile_StillResolvesThroughItsRetainedName()
    {
        using var builder = new SyntheticBlockBuilder('Y');
        var root = builder.AddRoot();
        var deletedRow = builder.AddRow("gone.tmp", root, RowFlags.InUse | RowFlags.Tombstone, 10, Moment);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('Y', 0, block, deleteFileOnRelease: false)]);
        try
        {
            var entry = FileEntry.Create(snapshot, 0, deletedRow);
            Assert.AreEqual(@"Y:\gone.tmp", entry.Path);
            Assert.IsTrue(entry.IsDeleted);
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Path_OfALiveFileUnderATombstonedDirectory_StillResolvesAndIsStillUnderIt()
    {
        using var builder = new SyntheticBlockBuilder('V');
        var root = builder.AddRoot();
        var deletedDirectoryRow = builder.AddRow("Old", root, RowFlags.InUse | RowFlags.Directory | RowFlags.Tombstone,
            0, Moment);
        var liveFileRow = builder.AddRow("survivor.txt", deletedDirectoryRow, RowFlags.InUse, 5, Moment);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('V', 0, block, deleteFileOnRelease: false)]);
        try
        {
            var liveFile = FileEntry.Create(snapshot, 0, liveFileRow);
            var deletedDirectory = FileEntry.Create(snapshot, 0, deletedDirectoryRow);

            Assert.AreEqual(@"V:\Old\survivor.txt", liveFile.Path);
            Assert.IsTrue(IndexNavigationTestAccess.IsUnder(liveFile, deletedDirectory));
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Children_ExcludesTombstonedRows()
    {
        using var builder = new SyntheticBlockBuilder('U');
        var root = builder.AddRoot();
        var liveRow = builder.AddRow("live.txt", root, RowFlags.InUse, 1, Moment);
        builder.AddRow("dead.txt", root, RowFlags.InUse | RowFlags.Tombstone, 1, Moment);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('U', 0, block, deleteFileOnRelease: false)]);
        try
        {
            var children = FileEntry.Create(snapshot, 0, root).Children();
            Assert.AreEqual(1, children.Count);
            Assert.AreEqual("live.txt", children[0].Name);
            Assert.AreEqual(liveRow, children[0].RowIndexForTest());
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Path_WithACyclicParentColumn_TruncatesInsteadOfHanging()
    {
        using var builder = new SyntheticBlockBuilder('W');
        builder.AddRoot();
        var first = builder.AddRow("a", 2, RowFlags.InUse | RowFlags.Directory, 0, Moment);
        var second = builder.AddRow("b", first, RowFlags.InUse | RowFlags.Directory, 0, Moment);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('W', 0, block, deleteFileOnRelease: false)]);
        try
        {
            // Rows 1 and 2 point at each other. The walk must stop rather than loop.
            var path = FileEntry.Create(snapshot, 0, second).Path;
            Assert.IsTrue(path.StartsWith(@"W:\", StringComparison.Ordinal));
            Assert.IsTrue(path.Length < 100);
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }

    [TestMethod]
    public void Path_DeeperThanTheDepthCap_IsTruncatedNotInfinite()
    {
        using var builder = new SyntheticBlockBuilder('X', slotCapacity: 512, namePoolCapacity: 16384);
        var root = builder.AddRoot();
        var parent = root;
        for (var level = 0; level < BlockLayout.MaximumPathDepth + 20; level++)
        {
            parent = builder.AddRow($"d{level}", parent, RowFlags.InUse | RowFlags.Directory, 0, Moment);
        }

        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        var snapshot = Snapshot.Create([new DriveBlock('X', 0, block, deleteFileOnRelease: false)]);
        try
        {
            var path = FileEntry.Create(snapshot, 0, parent).Path;
            var separators = path.Count(character => character == '\\');
            Assert.IsTrue(separators <= BlockLayout.MaximumPathDepth + 1,
                $"Path had {separators} separators, above the depth cap.");
        }
        finally
        {
            snapshot.ReleaseNow();
        }
    }
}

static class IndexNavigationTestAccess
{
    public static bool IsUnder(FileEntry candidate, FileEntry ancestor)
    {
        return IndexNavigationBridge.IsUnder(candidate, ancestor);
    }
}

static class FileEntryTestExtensions
{
    public static uint RowIndexForTest(this FileEntry entry)
    {
        return IndexNavigationBridge.RowIndexOf(entry);
    }
}
