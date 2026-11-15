using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953: a block lineage publishes one mapping at a time. Replacing it moves every view in one store,
///     keeps handle identity and lineage state, lets readers that pinned the retired mapping finish on it, and keeps
///     the retired mapping mapped until the lineage itself is released.
/// </summary>
[TestClass]
public sealed class BlockLineageTests
{
    const uint ReadmeRow = 31;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Replace_MovesEveryViewOntoTheReplacement_InOneStore()
    {
        using var fixture = new Lineage();
        var retired = fixture.Mapped.Current;

        var returned = fixture.Mapped.Replace(fixture.Replacement);

        Assert.AreSame(retired, returned);
        Assert.AreSame(fixture.Replacement, fixture.First.Block);
        Assert.AreSame(fixture.Replacement, fixture.Second.Block);
        Assert.AreSame(fixture.Mapped, fixture.First.Mapped);
        Assert.AreSame(fixture.Mapped, fixture.Second.Mapped);
    }

    [TestMethod]
    public void Replace_KeepsTheLineagesProducerKindAndReportedCycles()
    {
        using var fixture = new Lineage();
        var cycles = fixture.First.ReportedCycles;

        fixture.Mapped.Replace(fixture.Replacement);

        Assert.AreSame(cycles, fixture.First.ReportedCycles);
        Assert.AreSame(cycles, fixture.Second.ReportedCycles);
        Assert.AreEqual(ProducerKind.Mft, fixture.First.ProducerKind);
    }

    [TestMethod]
    public void HandleMintedBeforeReplace_StaysLiveAndEqual_AndReadsTheReplacement()
    {
        using var fixture = new Lineage();
        var before = FileEntry.Create(fixture.First, ReadmeRow);
        Assert.AreEqual("readme.md", before.Name);

        fixture.Mapped.Replace(fixture.Replacement);

        var after = FileEntry.Create(fixture.First, ReadmeRow);
        Assert.AreEqual(after, before);
        Assert.AreEqual(after.GetHashCode(), before.GetHashCode());
        Assert.IsFalse(before.IsDisposed);
        Assert.IsFalse(before.IsDeleted);
        Assert.AreEqual("renamed.md", before.Name);
        Assert.AreEqual(Lineage.ReplacementSize, before.Size);
    }

    [TestMethod]
    public void BorrowTakenBeforeReplace_ReadsTheRetiredMappingToTheEnd()
    {
        using var fixture = new Lineage();
        var retired = fixture.Mapped.Current;
        using var borrow = new SnapshotBorrow(fixture.First);

        fixture.Mapped.Replace(fixture.Replacement);

        Assert.AreSame(retired, borrow.Mapping);
        var pinned = borrow.Pinned!.Value;
        Assert.AreSame(retired.Block, pinned.Block);
        var found = SearchEngine.Search(pinned, new SearchQuery("readme.md", NameMatchMode.Exact),
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(ReadmeRow, found.Single().RowIndex);
        Assert.IsEmpty(SearchEngine.Search(pinned, new SearchQuery("renamed.md", NameMatchMode.Exact),
            cancellationToken: TestContext.CancellationToken));
        Assert.AreEqual(1, retired.ReleaseState.Hooks.OutstandingBorrowCount);
        Assert.AreEqual(0, fixture.Mapped.Current.ReleaseState.Hooks.OutstandingBorrowCount);
    }

    [TestMethod]
    public void BorrowTakenAfterReplace_PinsTheReplacement()
    {
        using var fixture = new Lineage();
        fixture.Mapped.Replace(fixture.Replacement);

        using var borrow = new SnapshotBorrow(fixture.Second);

        Assert.AreSame(fixture.Replacement, borrow.Pinned!.Value.Block);
        Assert.AreEqual("renamed.md", SearchEngine.Search(borrow.Pinned,
            new SearchQuery("renamed.md", NameMatchMode.Exact), cancellationToken: TestContext.CancellationToken)
            .Single().Name);
    }

    [TestMethod]
    public void BorrowThatRacesAReplace_ReturnsItsCountAndPinsTheReplacement()
    {
        using var fixture = new Lineage();
        var retired = fixture.Mapped.Current;
        var mapped = fixture.Mapped;
        var replacement = fixture.Replacement;
        var replacedOnce = false;
        mapped.Hooks._borrowCounted = () =>
        {
            if (!replacedOnce)
            {
                replacedOnce = true;
                mapped.Replace(replacement);
            }
        };

        using var borrow = new SnapshotBorrow(fixture.First);

        Assert.IsTrue(replacedOnce);
        Assert.AreSame(fixture.Mapped.Current, borrow.Mapping);
        Assert.AreSame(fixture.Replacement, borrow.Pinned!.Value.Block);
        Assert.AreEqual(0, retired.ReleaseState.Hooks.OutstandingBorrowCount);
        Assert.AreEqual(1, fixture.Mapped.Current.ReleaseState.Hooks.OutstandingBorrowCount);
    }

    [TestMethod]
    public async Task RetiredMapping_StaysMappedWithoutBorrows_UntilTheLineageIsReleased()
    {
        using var fixture = new Lineage();
        var retiredPath = fixture.RenameOriginalAside();
        var retired = fixture.Mapped.Replace(fixture.Replacement);
        retired.ReleaseState.ScheduleDeleteAt(retiredPath);

        Assert.IsFalse(retired.IsReleased);
        Assert.IsFalse(retired.ReleaseState.IsReleaseComplete);
        Assert.IsTrue(File.Exists(retiredPath));

        await fixture.Mapped.DrainAndReleaseAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.IsTrue(retired.ReleaseState.IsReleaseComplete);
        Assert.IsTrue(fixture.Mapped.Current.ReleaseState.IsReleaseComplete);
        Assert.IsFalse(File.Exists(retiredPath));
        Assert.IsTrue(fixture.First.IsReleased);
    }

    [TestMethod]
    public async Task LineageRelease_WaitsForTheRetiredMappingsLastBorrow_ThenDeletesItsFile()
    {
        using var fixture = new Lineage();
        var retiredPath = fixture.RenameOriginalAside();
        var borrow = new SnapshotBorrow(fixture.First);
        var retired = fixture.Mapped.Replace(fixture.Replacement);
        retired.ReleaseState.ScheduleDeleteAt(retiredPath);
        Task release;
        try
        {
            release = fixture.Mapped.DrainAndReleaseAsync().AsTask();

            Assert.IsFalse(release.IsCompleted);
            Assert.IsTrue(retired.IsReleased, "the retired mapping refuses new readers once its release begins");
            Assert.IsFalse(retired.ReleaseState.IsReleaseComplete, "a pinned mapping was unmapped under its reader");
            Assert.AreEqual("readme.md", NamePool.ReadRowName(borrow.Pinned!.Value.Block, ReadmeRow).ToString());
            Assert.IsTrue(File.Exists(retiredPath));
        }
        finally
        {
            borrow.Dispose();
        }

        await release.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.IsTrue(retired.ReleaseState.IsReleaseComplete);
        Assert.IsFalse(File.Exists(retiredPath));
        Assert.ThrowsExactly<ObjectDisposedException>(() => new SnapshotBorrow(fixture.First));
    }

    [TestMethod]
    public async Task Replace_AfterTheLineageIsSealed_ThrowsAndPublishesNothing()
    {
        using var fixture = new Lineage();
        var original = fixture.Mapped.Current;
        await fixture.Mapped.DrainAndReleaseAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.ThrowsExactly<ObjectDisposedException>(() => fixture.Mapped.Replace(fixture.Replacement));

        Assert.AreSame(original, fixture.Mapped.Current);
    }

    [TestMethod]
    public void Seal_ListsEveryMapping_AndRefusesLaterReplacement()
    {
        using var fixture = new Lineage();
        var original = fixture.Mapped.Current;
        fixture.Mapped.Replace(fixture.Replacement);

        var releases = fixture.Mapped.Seal();

        CollectionAssert.AreEqual(new[] { original.ReleaseState, fixture.Mapped.Current.ReleaseState }, releases);
        Assert.ThrowsExactly<ObjectDisposedException>(() => fixture.Mapped.Replace(fixture.Replacement));
    }

    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     One lineage over an MFT block at a real path, two views of it, and a replacement block in which the readme
    ///     row carries another name and size, so a read shows which mapping it went through.
    /// </summary>
    sealed class Lineage : IDisposable
    {
        internal const long ReplacementSize = 4096;
        static readonly DateTime ScanMoment = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
        readonly TempDirectory _directory = new("mftlib-lineage");
        readonly BlockFile _original;

        internal Lineage()
        {
            OriginalPath = Path.Combine(_directory.Path, "original.mlix");
            _original = Seed(OriginalPath, "readme.md", 12);
            Replacement = Seed(Path.Combine(_directory.Path, "replacement.mlix"), "renamed.md", ReplacementSize);
            Mapped = new MappedBlock(_original);
            First = new RootBlock(new VolumeKey('T'), Mapped, FirstBorrows, null, null, null);
            Second = new RootBlock(new VolumeKey('T'), Mapped, new ViewBorrows(), null, null, null);
        }

        internal string OriginalPath { get; }
        internal BlockFile Replacement { get; }
        internal MappedBlock Mapped { get; }
        internal ViewBorrows FirstBorrows { get; } = new();
        internal RootBlock First { get; }
        internal RootBlock Second { get; }

        /// <summary>Renames the original block's file aside, as a growth does before it publishes, and returns the new path.</summary>
        internal string RenameOriginalAside()
        {
            var retiredPath = OriginalPath + ".retired-1";
            File.Move(OriginalPath, retiredPath);
            return retiredPath;
        }

        static BlockFile Seed(string path, string readmeName, long readmeSize)
        {
            var block = TestBlocks.Create(path, ProducerKind.Mft, rootRow: 5, deleteOnClose: false);
            var writer = new BlockWriter(block);
            Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(5, "root", new RowColumns(5, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 1)));
            Assert.AreEqual(WriteRefusal.None, writer.TryWriteRow(ReadmeRow, readmeName,
                new RowColumns(5, RowFlags.InUse, 0x20, readmeSize, 0, 3)));
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
            _original.Dispose();
            _directory.Dispose();
        }
    }
}
