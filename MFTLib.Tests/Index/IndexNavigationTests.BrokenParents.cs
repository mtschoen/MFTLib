using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Issue 980: a parent link that names a slot no producer wrote, or a live row that is not a directory, is a
///     broken link. Before the fix the walk climbed through it into whatever the slot's zero parent column named,
///     which in an MFT block is row 0 (<c>$MFT</c>), and rendered <c>T:\$MFT\file</c>.
/// </summary>
public partial class IndexNavigationTests
{
    const uint NeverWrittenRow = 7;

    // The MFT row geometry the report came from: row 0 is the $MFT file whose parent is the root, the root is row 5,
    // and row 7 is a slot no producer wrote, so it reads as all zeros.
    static SyntheticBlockBuilder MftBlockWithNeverWrittenSlot()
    {
        var builder = new SyntheticBlockBuilder();
        builder.MutateHeader((ref header) =>
        {
            header.ProducerKind = ProducerKind.Mft;
            header.RootRow = 5;
            header.ScanShape = SyntheticScanShape.Default(ProducerKind.Mft);
        });
        builder.AddRowAt(0, "$MFT", new RowColumns(5, RowFlags.InUse, 0, 0, Moment.Ticks, 1));
        builder.AddRowAt(5, ".", new RowColumns(5, RowFlags.InUse | RowFlags.Directory, 0, 0, Moment.Ticks, 5));
        builder.AddRowAt(6, "Documents", new RowColumns(5, RowFlags.InUse | RowFlags.Directory, 0, 0, Moment.Ticks, 1));
        return builder;
    }

    static RootBlock[] OpenSnapshot(SyntheticBlockBuilder builder)
    {
        builder.Complete(Moment);
        var block = builder.OpenForReading(out _)!;
        return [new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: TestRootDirectory.For('T'))];
    }

    static string Rooted(params string[] names) => Path.Combine([TestRootDirectory.For('T'), .. names]);

    [TestMethod]
    public async Task Path_UnderANeverWrittenParentSlot_IsTheBareName_Issue980()
    {
        using var builder = MftBlockWithNeverWrittenSlot();
        var file = builder.AddRowAt(9, "TypeDB.mem.xml", new RowColumns(NeverWrittenRow, RowFlags.InUse, 0, 10, Moment.Ticks, 1));
        var snapshot = OpenSnapshot(builder);
        try
        {
            var entry = FileEntry.Create(snapshot[0], file);
            Assert.AreEqual("TypeDB.mem.xml", entry.Path);
            Assert.IsNull(entry.Parent);
            Assert.IsFalse(IsUnder(snapshot[0].Block, file, 5));
            Assert.IsFalse(IsUnder(snapshot[0].Block, file, 0));
            Assert.AreEqual("TypeDB.mem.xml",
                IndexNavigation.BuildChangePath(new PinnedRootBlock(snapshot[0], snapshot[0].Block), file));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Path_UnderAWrittenDirectoryWhoseParentSlotWasNeverWritten_IsUnrootedAtThatDirectory_Issue980()
    {
        using var builder = MftBlockWithNeverWrittenSlot();
        var directory = builder.AddRowAt(8, "r", new RowColumns(NeverWrittenRow, RowFlags.InUse | RowFlags.Directory, 0, 0, Moment.Ticks, 1));
        var file = builder.AddRowAt(9, "x.mui", new RowColumns(directory, RowFlags.InUse, 0, 10, Moment.Ticks, 1));
        var snapshot = OpenSnapshot(builder);
        try
        {
            var entry = FileEntry.Create(snapshot[0], file);
            Assert.AreEqual(Path.Combine("r", "x.mui"), entry.Path);
            Assert.AreEqual(directory, entry.Parent?.RowIndex);
            Assert.IsNull(entry.Parent!.Value.Parent);
            Assert.IsTrue(IsUnder(snapshot[0].Block, file, directory));
            Assert.IsFalse(IsUnder(snapshot[0].Block, file, 5));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Path_UnderALiveFileUsedAsAParent_IsTheBareName_Issue980()
    {
        using var builder = MftBlockWithNeverWrittenSlot();
        var file = builder.AddRowAt(9, "d93f.customDestinations-ms", new RowColumns(6, RowFlags.InUse, 0, 10, Moment.Ticks, 1));
        var child = builder.AddRowAt(10, "events.js", new RowColumns(file, RowFlags.InUse, 0, 10, Moment.Ticks, 1));
        var snapshot = OpenSnapshot(builder);
        try
        {
            var childEntry = FileEntry.Create(snapshot[0], child);
            var fileEntry = FileEntry.Create(snapshot[0], file);
            Assert.AreEqual("events.js", childEntry.Path);
            Assert.IsNull(childEntry.Parent);
            Assert.IsFalse(IsUnder(snapshot[0].Block, child, 6));
            Assert.IsFalse(IsUnder(snapshot[0].Block, child, file));
            Assert.IsEmpty(fileEntry.Children(TestContext.CancellationToken));
            Assert.AreEqual(Rooted("Documents", "d93f.customDestinations-ms"), fileEntry.Path);
            var pinned = new PinnedRootBlock(snapshot[0], snapshot[0].Block);
            Assert.IsNull(LookupEngine.Find(pinned, Rooted("Documents", "d93f.customDestinations-ms", "events.js"),
                cancellationToken: TestContext.CancellationToken));
            Assert.AreEqual(file, LookupEngine.Find(pinned, Rooted("Documents", "d93f.customDestinations-ms"),
                cancellationToken: TestContext.CancellationToken)?.RowIndex);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Path_UnderTheMftFileRow_IsTheBareName_Issue980()
    {
        using var builder = MftBlockWithNeverWrittenSlot();
        var child = builder.AddRowAt(9, "TypeDB.mem.xml", new RowColumns(0, RowFlags.InUse, 0, 10, Moment.Ticks, 1));
        var snapshot = OpenSnapshot(builder);
        try
        {
            Assert.AreEqual("TypeDB.mem.xml", FileEntry.Create(snapshot[0], child).Path);
            Assert.AreEqual(Rooted("$MFT"), FileEntry.Create(snapshot[0], 0).Path);
            Assert.IsEmpty(FileEntry.Create(snapshot[0], 0).Children(TestContext.CancellationToken));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    // Freed rows that FreedRowTrust kept attached, and journal tombstones, are deleted directories that still hold
    // their in-use flag and their directory flag, so the walk keeps passing through them.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Path_ThroughADeletedDirectoryChain_StaysRooted_Issue980(bool isDeletePending)
    {
        using var builder = MftBlockWithNeverWrittenSlot();
        var deletedFlag = isDeletePending ? RowFlags.DeletePending : RowFlags.Tombstone;
        var storedName = isDeletePending ? Path.Combine("Documents", "Old") : "Old";
        if (isDeletePending)
        {
            builder.AddRowAt(8, "Old", new RowColumns(6, RowFlags.InUse | RowFlags.Directory, 0, 0, Moment.Ticks, 1));
        }

        var deletedDirectory = builder.AddRowAt(8, storedName,
            new RowColumns(6, RowFlags.InUse | RowFlags.Directory | deletedFlag, 0, 0, Moment.Ticks, 2));
        var freedDirectory = builder.AddRowAt(9, "Older",
            new RowColumns(deletedDirectory, RowFlags.InUse | RowFlags.Directory | RowFlags.Tombstone, 0, 0, Moment.Ticks, 2));
        var freedFile = builder.AddRowAt(10, "gone.bin",
            new RowColumns(freedDirectory, RowFlags.InUse | RowFlags.Tombstone, 0, 3, Moment.Ticks, 2));
        var liveFile = builder.AddRowAt(11, "survivor.txt",
            new RowColumns(deletedDirectory, RowFlags.InUse, 0, 3, Moment.Ticks, 2));
        var snapshot = OpenSnapshot(builder);
        try
        {
            var freed = FileEntry.Create(snapshot[0], freedFile);
            Assert.AreEqual(Rooted("Documents", "Old", "Older", "gone.bin"), freed.Path);
            Assert.AreEqual(freedDirectory, freed.Parent?.RowIndex);
            Assert.AreEqual(deletedDirectory, freed.Parent!.Value.Parent?.RowIndex);
            Assert.AreEqual(Rooted("Documents", "Old", "survivor.txt"), FileEntry.Create(snapshot[0], liveFile).Path);
            Assert.IsTrue(IsUnder(snapshot[0].Block, freedFile, 6));
            Assert.IsTrue(IsUnder(snapshot[0].Block, liveFile, deletedDirectory));
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }

    [TestMethod]
    public async Task Path_OfARowWhoseNameIdentifierCarriesTheHighBit_ThrowsLikeTheNameRead()
    {
        using var builder = MftBlockWithNeverWrittenSlot();
        var file = builder.AddRowAt(9, "report.pdf", new RowColumns(6, RowFlags.InUse, 0, 10, Moment.Ticks, 1));
        var publishedId = builder.OpenForWriting().Rows[(int)file].NameId;
        builder.Complete(Moment);
        builder.MutateNameDescriptor(file, publishedId | RootKind.TerminalSegmentFlag);
        var block = builder.OpenForReading(out _)!;
        var snapshot = new[] { new RootBlock(new VolumeKey('T'), block, rootDirectoryPath: TestRootDirectory.For('T')) };
        try
        {
            var entry = FileEntry.Create(snapshot[0], file);
            Assert.ThrowsExactly<InvalidDataException>(() => entry.Name);
            Assert.ThrowsExactly<InvalidDataException>(() => entry.Path);
        }
        finally
        {
            await snapshot.ReleaseNowAsync();
        }
    }
}
