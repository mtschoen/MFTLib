using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[SuppressMessage("Design", "CA1001",
    Justification = "Cleanup is [TestCleanup], the MSTest-idiomatic disposal path this test project uses " +
                     "throughout rather than IDisposable on the test class itself.")]
public partial class IndexNavigationTests
{
    static readonly DateTime Moment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    SyntheticBlockBuilder _builder = null!;
    RootBlock[] _snapshot = null!;
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
        _documentsRow = _builder.AddRow("Documents", _rootRow, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        _projectsRow = _builder.AddRow("Projects", _documentsRow, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        _reportRow = _builder.AddRow("report.pdf", _projectsRow, RowFlags.InUse, 4096, Moment, sequenceNumber: 0);
        _notesRow = _builder.AddRow("notes.txt", _documentsRow, RowFlags.InUse, 128, Moment, sequenceNumber: 0);
        _builder.Complete(Moment);

        var block = _builder.OpenForReading(out _)!;
        _snapshot = [new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: TestRootDirectory.For('T'))];
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _snapshot.ReleaseNowAsync();
        _builder.Dispose();
    }

    FileEntry Entry(uint rowIndex)
    {
        return FileEntry.Create(_snapshot[0], rowIndex);
    }

    [TestMethod]
    public void Path_JoinsNamesFromTheRootDown()
    {
        Assert.AreEqual(Path.Combine(TestRootDirectory.For('T'), "Documents", "Projects", "report.pdf"), Entry(_reportRow).Path);
        Assert.AreEqual(Path.Combine(TestRootDirectory.For('T'), "Documents", "notes.txt"), Entry(_notesRow).Path);
    }

    [TestMethod]
    public void Path_OfTheRootIsTheRootRoot()
    {
        Assert.AreEqual(TestRootDirectory.For('T'), Entry(_rootRow).Path);
    }

    [TestMethod]
    public void Parent_WalksOneLevelUpAndStopsAtTheRoot()
    {
        var report = Entry(_reportRow);
        var projects = report.Parent;
        Assert.IsTrue(projects.HasValue);
        Assert.AreEqual("Projects", projects.GetValueOrDefault().Name);

        var root = Entry(_rootRow);
        Assert.IsNull(root.Parent);
    }

    [TestMethod]
    public void Children_ReturnsDirectChildrenOnly()
    {
        var children = Entry(_documentsRow).Children(TestContext.CancellationToken);
        var names = children.Select(child => child.Name).Order().ToArray();

        Assert.AreSequenceEqual(["notes.txt", "Projects"], names);
    }

    [TestMethod]
    public void Children_OfAFileIsEmpty()
    {
        Assert.IsEmpty(Entry(_reportRow).Children(TestContext.CancellationToken));
    }

    [TestMethod]
    public void Children_DoesNotReturnTheRootAsItsOwnChild()
    {
        var children = Entry(_rootRow).Children(TestContext.CancellationToken);
        Assert.DoesNotContain(child => child.RowIndex == _rootRow, children);
        Assert.HasCount(1, children);
    }

    [TestMethod]
    public void IsUnder_MatchesTransitiveContainmentAndExcludesSiblings()
    {
        var block = _snapshot[0].Block;
        Assert.IsTrue(IndexNavigation.IsUnder(block, _reportRow, _documentsRow));
        Assert.IsTrue(IndexNavigation.IsUnder(block, _reportRow, _projectsRow));
        Assert.IsFalse(IndexNavigation.IsUnder(block, _notesRow, _projectsRow));
        Assert.IsTrue(IndexNavigation.IsUnder(block, _projectsRow, _projectsRow));
    }

    [TestMethod]
    public void SubtreeQueries_ExcludeOtherRootsAndRejectDefaultAnchors()
    {
        using var harness = new WatchHarness('T', 'U');
        var set = new FileIndexSet([harness.IndexFor('T'), harness.IndexFor('U')]);
        var firstRoot = harness.IndexFor('T').Root(TestContext.CancellationToken);
        var secondRoot = harness.IndexFor('U').Root(TestContext.CancellationToken);

        Assert.DoesNotContain(entry => entry.Root == new VolumeKey('U'), set.Search(new SearchQuery(null, Subtree: firstRoot), TestContext.CancellationToken));
        Assert.DoesNotContain(entry => entry.Root == new VolumeKey('T'), set.Search(new SearchQuery(null, Subtree: secondRoot), TestContext.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() =>
            set.Search(new SearchQuery(null, Subtree: default(FileEntry)), TestContext.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() =>
            set.Enumerate(new SearchQuery(null, Subtree: default(FileEntry)), TestContext.CancellationToken));
    }

    /// <summary>
    ///     The walk hits the depth cap without reaching the target, and the row it stands on
    ///     turns out to be self-parented (a root): that is a truncated chain, not a deep
    ///     match, so the answer is false rather than the depth-exceeded throw.
    /// </summary>
    [TestMethod]
    public async Task IsUnder_WhenTheCappedChainEndsAtARoot_ReturnsFalse()
    {
        using var builder = new SyntheticBlockBuilder('Z', slotCapacity: 512, namePoolCapacity: 16384);
        var root = builder.AddRoot();
        var unrelated = builder.AddRow("unrelated", root, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        var candidate = root;
        for (var level = 0; level < BlockLayout.MaximumPathDepth; level++)
        {
            candidate = builder.AddRow($"d{level}", candidate, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        }

        builder.Complete(Moment);
        var block = builder.OpenForReading(out _)!;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('Z'), block, rootDirectoryPath: TestRootDirectory.For('Z'))];
        try
        {
            // The candidate sits exactly MaximumPathDepth hops below the root; the target
            // is a real row that is not on that chain.
            Assert.IsFalse(IndexNavigation.IsUnder(block, candidate, unrelated));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Path_OfATombstonedFile_StillResolvesThroughItsRetainedName()
    {
        using var builder = new SyntheticBlockBuilder('Y');
        var root = builder.AddRoot();
        var deletedRow = builder.AddRow("gone.tmp", root, RowFlags.InUse | RowFlags.Tombstone, 10, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('Y'), block, rootDirectoryPath: TestRootDirectory.For('Y'))];
        try
        {
            var entry = FileEntry.Create(snapshot[0], deletedRow);
            Assert.AreEqual(Path.Combine(TestRootDirectory.For('Y'), "gone.tmp"), entry.Path);
            Assert.IsTrue(entry.IsDeleted);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Path_OfALiveFileUnderATombstonedDirectory_StillResolvesAndIsStillUnderIt()
    {
        using var builder = new SyntheticBlockBuilder('V');
        var root = builder.AddRoot();
        var deletedDirectoryRow = builder.AddRow("Old", root, RowFlags.InUse | RowFlags.Directory | RowFlags.Tombstone,
            0, Moment, sequenceNumber: 0);
        var liveFileRow = builder.AddRow("survivor.txt", deletedDirectoryRow, RowFlags.InUse, 5, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('V'), block, rootDirectoryPath: TestRootDirectory.For('V'))];
        try
        {
            var liveFile = FileEntry.Create(snapshot[0], liveFileRow);

            Assert.AreEqual(Path.Combine(TestRootDirectory.For('V'), "Old", "survivor.txt"), liveFile.Path);
            Assert.IsTrue(IndexNavigation.IsUnder(block, liveFileRow, deletedDirectoryRow));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Children_ExcludesTombstonedRows()
    {
        using var builder = new SyntheticBlockBuilder('U');
        var root = builder.AddRoot();
        var liveRow = builder.AddRow("live.txt", root, RowFlags.InUse, 1, Moment, sequenceNumber: 0);
        builder.AddRow("dead.txt", root, RowFlags.InUse | RowFlags.Tombstone, 1, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('U'), block, rootDirectoryPath: TestRootDirectory.For('U'))];
        try
        {
            var children = FileEntry.Create(snapshot[0], root).Children(TestContext.CancellationToken);
            Assert.HasCount(1, children);
            Assert.AreEqual("live.txt", children[0].Name);
            Assert.AreEqual(liveRow, children[0].RowIndex);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Path_WithACyclicParentColumn_TruncatesInsteadOfHanging()
    {
        using var builder = new SyntheticBlockBuilder('W');
        builder.AddRoot();
        var first = builder.AddRow("a", 2, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        var second = builder.AddRow("b", first, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('W'), block, rootDirectoryPath: TestRootDirectory.For('W'))];
        try
        {
            // Rows 1 and 2 point at each other. The walk must stop rather than loop.
            var path = FileEntry.Create(snapshot[0], second).Path;
            Assert.IsTrue(path.StartsWith(TestRootDirectory.For('W'), StringComparison.Ordinal));
            Assert.IsLessThan(100, path.Length);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Path_AtTheDepthCap_ResolvesTheCompletePath()
    {
        using var builder = new SyntheticBlockBuilder('X', slotCapacity: 512, namePoolCapacity: 16384);
        var root = builder.AddRoot();
        var parent = root;
        for (var level = 0; level < BlockLayout.MaximumPathDepth; level++)
        {
            parent = builder.AddRow($"d{level}", parent, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        }

        builder.Complete(Moment);
        var block = builder.OpenForReading(out _)!;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('X'), block, rootDirectoryPath: TestRootDirectory.For('X'))];
        try
        {
            var path = FileEntry.Create(snapshot[0], parent).Path;
            Assert.IsTrue(path.StartsWith(Path.Combine(TestRootDirectory.For('X'), "d0", "d1"), StringComparison.Ordinal));
            Assert.IsTrue(path.EndsWith($"{Path.DirectorySeparatorChar}d127", StringComparison.Ordinal));
            Assert.AreEqual(BlockLayout.MaximumPathDepth, path[TestRootDirectory.For('X').TrimEnd(Path.DirectorySeparatorChar).Length..]
                .Count(character => character == Path.DirectorySeparatorChar));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Path_DeeperThanTheDepthCap_ThrowsInvalidDataException()
    {
        using var builder = new SyntheticBlockBuilder('X', slotCapacity: 512, namePoolCapacity: 16384);
        var root = builder.AddRoot();
        var parent = root;
        for (var level = 0; level <= BlockLayout.MaximumPathDepth; level++)
        {
            parent = builder.AddRow($"d{level}", parent, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        }

        builder.Complete(Moment);
        var block = builder.OpenForReading(out _)!;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('X'), block, rootDirectoryPath: TestRootDirectory.For('X'))];
        try
        {
            var exception = Assert.ThrowsExactly<InvalidDataException>(
                () => _ = FileEntry.Create(snapshot[0], parent).Path);
            Assert.Contains(BlockLayout.MaximumPathDepth.ToString(CultureInfo.InvariantCulture), exception.Message);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task IsUnder_AncestorExactlyAtTheDepthCap_ReturnsTrue()
    {
        using var builder = new SyntheticBlockBuilder('Z', slotCapacity: 512, namePoolCapacity: 16384);
        var root = builder.AddRoot();
        var candidate = root;
        for (var level = 0; level < BlockLayout.MaximumPathDepth; level++)
        {
            candidate = builder.AddRow($"d{level}", candidate, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        }

        builder.Complete(Moment);
        var block = builder.OpenForReading(out _)!;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('Z'), block, rootDirectoryPath: TestRootDirectory.For('Z'))];
        try
        {
            Assert.IsTrue(IndexNavigation.IsUnder(block, candidate, root));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task IsUnder_WithACyclicParentColumn_ReturnsFalseInsteadOfThrowing()
    {
        using var builder = new SyntheticBlockBuilder('W');
        var root = builder.AddRoot();
        var first = builder.AddRow("a", 2, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        var second = builder.AddRow("b", first, RowFlags.InUse | RowFlags.Directory, 0, Moment, sequenceNumber: 0);
        var child = builder.AddRow("c", second, RowFlags.InUse, 10, Moment, sequenceNumber: 0);
        builder.Complete(Moment);

        var block = builder.OpenForReading(out _)!;
        RootBlock[] snapshot = [new RootBlock(new VolumeKey('W'), block, rootDirectoryPath: TestRootDirectory.For('W'))];
        try
        {
            // Rows 1 and 2 point at each other. None of these reach root.
            Assert.IsFalse(IndexNavigation.IsUnder(block, second, root));
            Assert.IsFalse(IndexNavigation.IsUnder(block, first, root));
            Assert.IsFalse(IndexNavigation.IsUnder(block, child, root));

            // But rows in the cycle or under the cycle are under cycle members.
            Assert.IsTrue(IndexNavigation.IsUnder(block, second, first));
            Assert.IsTrue(IndexNavigation.IsUnder(block, first, second));
            Assert.IsTrue(IndexNavigation.IsUnder(block, child, first));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
