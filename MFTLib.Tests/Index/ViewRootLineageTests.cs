using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953: a directory view's binding is shared by every query of the view and follows the lineage's
///     current mapping. A query that pinned a mapping its lineage has since retired admits against that mapping for
///     itself and never publishes what it saw there into the shared binding.
/// </summary>
[TestClass]
public sealed class ViewRootLineageTests
{
    const uint RootRow = 5;
    const uint TargetRow = 10;

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    ///     The interleaving the crew review replayed: a query pins the original mapping and pauses before admission; the
    ///     directory is renamed away on the original, the replacement is published in that state, and the directory is
    ///     renamed back on the replacement, which the current binding follows. The paused query then admits.
    /// </summary>
    [TestMethod]
    public void StaleAdmission_AfterRenameAwayReplaceAndRenameBack_LeavesTheCurrentBindingPresent()
    {
        using var fixture = new DirectoryView();
        Assert.IsTrue(fixture.View.Resolve(fixture.RootBlock, TestContext.CancellationToken));
        using var stale = new SnapshotBorrow(fixture.RootBlock);
        Assert.AreEqual(WriteRefusal.None, new BlockWriter(fixture.Original).TryRenameRow(TargetRow, "away", RootRow));
        fixture.Mapped.Replace(fixture.Replacement);
        Assert.AreEqual(WriteRefusal.None, new BlockWriter(fixture.Replacement).TryRenameRow(TargetRow, "target", RootRow));
        Assert.IsTrue(fixture.View.Resolve(fixture.RootBlock, TestContext.CancellationToken));

        var staleAdmitted = fixture.View.TryAdmit(stale.Pinned!.Value, out _);

        Assert.IsFalse(staleAdmitted, "the retired mapping has the directory renamed away");
        Assert.IsTrue(fixture.View.IsPresent, "a stale query published its absence into the shared binding");
        using var current = new SnapshotBorrow(fixture.RootBlock);
        Assert.IsTrue(fixture.View.TryAdmit(current.Pinned!.Value, out var row), "the current query lost the directory");
        Assert.AreEqual(TargetRow, row);
        Assert.IsTrue(fixture.View.IsPresent);
    }

    /// <summary>
    ///     The mirror case: the directory is present on the retired mapping but renamed away on the current one, and
    ///     the current binding records it absent. The stale query admits on its own mapping, and the shared binding
    ///     stays absent, so a current query still answers absent without a descent.
    /// </summary>
    [TestMethod]
    public void StaleAdmission_OfADirectoryGoneFromTheCurrentMapping_LeavesTheCurrentBindingAbsent()
    {
        using var fixture = new DirectoryView();
        Assert.IsTrue(fixture.View.Resolve(fixture.RootBlock, TestContext.CancellationToken));
        Assert.AreEqual(WriteRefusal.None, new BlockWriter(fixture.Original).TryRenameRow(TargetRow, "away", RootRow));
        Assert.IsFalse(fixture.View.Resolve(fixture.RootBlock, TestContext.CancellationToken));
        Assert.AreEqual(WriteRefusal.None, new BlockWriter(fixture.Original).TryRenameRow(TargetRow, "target", RootRow));
        Assert.IsTrue(fixture.View.Resolve(fixture.RootBlock, TestContext.CancellationToken));
        using var stale = new SnapshotBorrow(fixture.RootBlock);
        fixture.Mapped.Replace(fixture.Replacement);
        Assert.IsFalse(fixture.View.Resolve(fixture.RootBlock, TestContext.CancellationToken));

        Assert.IsTrue(fixture.View.TryAdmit(stale.Pinned!.Value, out var staleRow));

        Assert.AreEqual(TargetRow, staleRow);
        Assert.IsFalse(fixture.View.IsPresent, "a stale query published its presence into the shared binding");
        var descents = fixture.View.ResolutionDescents;
        using var current = new SnapshotBorrow(fixture.RootBlock);
        Assert.IsFalse(fixture.View.TryAdmit(current.Pinned!.Value, out _));
        Assert.AreEqual(descents, fixture.View.ResolutionDescents, "the current absence lost its no-descent answer");
    }

    /// <summary>
    ///     An absence resolved on a mapping that a replacement retired before the verdict was stored (the window between a
    ///     query's currency check and its publication) is not trusted for the replacement: the next current admission
    ///     looks the path up again.
    /// </summary>
    [TestMethod]
    public void AbsenceRecordedOnARetiredMapping_IsResolvedAgainOnTheCurrentMapping()
    {
        using var fixture = new DirectoryView();
        Assert.AreEqual(WriteRefusal.None, new BlockWriter(fixture.Original).TryRenameRow(TargetRow, "away", RootRow));
        Assert.IsFalse(fixture.View.Resolve(fixture.RootBlock, TestContext.CancellationToken));
        fixture.Mapped.Replace(fixture.Replacement);
        Assert.AreEqual(WriteRefusal.None, new BlockWriter(fixture.Replacement).TryRenameRow(TargetRow, "target", RootRow));

        using var current = new SnapshotBorrow(fixture.RootBlock);

        Assert.IsTrue(fixture.View.TryAdmit(current.Pinned!.Value, out var row));
        Assert.AreEqual(TargetRow, row);
        Assert.IsTrue(fixture.View.IsPresent);
    }

    /// <summary>
    ///     A lookup on a directory view walks down from the row its admission bound; without one it refuses rather than
    ///     fall back on the shared binding, which may describe a newer mapping than the one the lookup pinned.
    /// </summary>
    [TestMethod]
    public void Lookup_OnADirectoryViewWithoutAnAdmittedRow_Refuses()
    {
        using var fixture = new DirectoryView();
        Assert.IsTrue(fixture.View.Resolve(fixture.RootBlock, TestContext.CancellationToken));
        using var borrow = new SnapshotBorrow(fixture.RootBlock);
        var viewPath = Path.Combine(fixture.VolumePath, "target");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            LookupEngine.Find(borrow.Pinned, viewPath, cancellationToken: TestContext.CancellationToken));
        Assert.IsTrue(fixture.View.TryAdmit(borrow.Pinned!.Value, out var row));
        Assert.AreEqual(TargetRow, LookupEngine.Find(borrow.Pinned, viewPath, row, TestContext.CancellationToken)
            ?.RecordKey.RecordNumber);
    }

    /// <summary>
    ///     One lineage over a synthetic MFT block (root row 5, directory row 10 named "target" under it), a directory
    ///     view of that directory, and a replacement block whose directory row is renamed away.
    /// </summary>
    sealed class DirectoryView : IDisposable
    {
        static readonly DateTime ScanMoment = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
        readonly TempDirectory _volume = new("mftlib-view-volume");

        internal DirectoryView()
        {
            Original = Seed("target");
            Replacement = Seed("away");
            Mapped = new MappedBlock(Original);
            var kind = RootKind.For(ProducerKind.Mft, isDump: false);
            View = ViewRoot.ForDirectory(Path.Combine(_volume.Path, "target"), kind, _volume.Path);
            RootBlock = new RootBlock(new VolumeKey('T'), Mapped, new ViewBorrows(), _volume.Path, null, View);
        }

        internal string VolumePath => _volume.Path;
        internal BlockFile Original { get; }
        internal BlockFile Replacement { get; }
        internal MappedBlock Mapped { get; }
        internal ViewRoot View { get; }
        internal RootBlock RootBlock { get; }

        static BlockFile Seed(string directoryName)
        {
            var block = TestBlocks.Create(producer: ProducerKind.Mft, rootRow: RootRow);
            var writer = new BlockWriter(block);
            Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(RootRow, ".",
                new RowColumns(RootRow, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 1)));
            Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(TargetRow, directoryName,
                new RowColumns(RootRow, RowFlags.InUse | RowFlags.Directory, 0x10, 0, 0, 2)));
            writer.Complete(ScanMoment, null);
            return block;
        }

        public void Dispose()
        {
            foreach (var release in Mapped.Seal())
            {
                release.ReleaseUnborrowed();
            }

            Replacement.Dispose();
            Original.Dispose();
            _volume.Dispose();
        }
    }
}
