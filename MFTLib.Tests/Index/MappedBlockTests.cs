using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class MappedBlockTests
{
    [TestMethod]
    public void TwoViewHandles_ShareOneMapping()
    {
        using var fixture = new SharedMapping();

        Assert.AreSame(fixture.First.Block, fixture.Second.Block);
        Assert.AreSame(fixture.First.ReportedCycles, fixture.Second.ReportedCycles);
        Assert.AreNotSame(fixture.First, fixture.Second);
    }

    [TestMethod]
    public async Task ClosingOneView_RefusesItsBorrowsOnly()
    {
        using var fixture = new SharedMapping();

        await fixture.FirstBorrows.CloseAndDrainAsync();

        Assert.IsFalse(fixture.First.TryTakeBorrow());
        using var other = new SnapshotBorrow(fixture.Second);
        Assert.AreEqual(1, fixture.SecondBorrows.OutstandingCount);
        Assert.IsFalse(fixture.Mapped.IsReleased);
    }

    [TestMethod]
    public async Task CloseAndDrain_WaitsForItsOwnBorrowsOnly()
    {
        using var fixture = new SharedMapping();
        var first = new SnapshotBorrow(fixture.First);
        using var second = new SnapshotBorrow(fixture.Second);
        Task drained;
        try
        {
            drained = fixture.FirstBorrows.CloseAndDrainAsync();
            Assert.IsFalse(drained.IsCompleted);
            Assert.AreSame(drained, fixture.FirstBorrows.CloseAndDrainAsync());
        }
        finally
        {
            first.Dispose();
        }
        await drained.WaitAsync(HangGuard);

        Assert.AreEqual(0, fixture.FirstBorrows.OutstandingCount);
        Assert.AreEqual(1, fixture.SecondBorrows.OutstandingCount);
        Assert.AreEqual("a.txt", FileEntry.Create(fixture.Second, 31).Name);
    }

    [TestMethod]
    public async Task ClosedView_EntryThrowsObjectDisposed_WhileTheOtherViewReads()
    {
        using var fixture = new SharedMapping();
        var first = FileEntry.Create(fixture.First, 31);
        var second = FileEntry.Create(fixture.Second, 31);

        await fixture.FirstBorrows.CloseAndDrainAsync();

        Assert.ThrowsExactly<ObjectDisposedException>(() => first.Name);
        Assert.IsTrue(first.IsDisposed);
        Assert.AreEqual("a.txt", second.Name);
        Assert.IsFalse(second.IsDisposed);
    }

    [TestMethod]
    public void EntriesOfTwoViews_OfOneRow_AreNotEqual()
    {
        using var fixture = new SharedMapping();
        var first = FileEntry.Create(fixture.First, 31);
        var second = FileEntry.Create(fixture.Second, 31);

        Assert.AreNotEqual(first, second);
        Assert.AreEqual(first, FileEntry.Create(fixture.First, 31));
    }

    [TestMethod]
    public void WithRootBlock_KeepsTheCapturedSequence()
    {
        using var fixture = new SharedMapping();
        var first = FileEntry.Create(fixture.First, 31);
        fixture.Mapped.Block.SequenceNumbers[31] = 2;

        var rebound = first.WithRootBlock(fixture.Second);

        Assert.IsTrue(rebound.IsDeleted);
        Assert.AreSame(fixture.Second, rebound.NamedRootBlock);
        Assert.AreNotEqual(FileEntry.Create(fixture.Second, 31), rebound);
    }

    [TestMethod]
    public void Anchor_FromAnotherViewOfTheBlock_IsRejected()
    {
        using var fixture = new SharedMapping();
        var anchor = FileEntry.Create(fixture.First, 5);
        var query = new SearchQuery("a", Subtree: anchor);

        var exception = Assert.ThrowsExactly<ArgumentException>(() => SubtreeAnchor.Resolve(fixture.Second, query));

        Assert.Contains("another view", exception.Message);
        Assert.IsNotNull(SubtreeAnchor.Resolve(fixture.First, query));
    }

    [TestMethod]
    public void ReleasingTheMapping_ReleasesEveryView()
    {
        using var fixture = new SharedMapping();

        fixture.Mapped.ReleaseState.ReleaseUnborrowed();

        Assert.IsTrue(fixture.First.IsReleased);
        Assert.IsTrue(fixture.Second.IsReleased);
        Assert.IsFalse(fixture.First.TryTakeBorrow());
        Assert.AreEqual(0, fixture.FirstBorrows.OutstandingCount);
    }

    sealed class SharedMapping : IDisposable
    {
        readonly BlockFile _block = TestBlocks.Create(producer: ProducerKind.Mft, rootRow: 5);

        internal SharedMapping()
        {
            Mapped = new MappedBlock(_block);
            var writer = new BlockWriter(_block);
            Assert.IsTrue(writer.TryWriteRow(5, "root", new RowColumns(5, RowFlags.InUse | RowFlags.Directory, 0, 0, 0, 1)));
            Assert.IsTrue(writer.TryWriteRow(31, "a.txt", new RowColumns(5, RowFlags.InUse, 1, 0, 0, 1)));
            writer.Complete(new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc), null);
            First = new RootBlock(new VolumeKey('T'), Mapped, FirstBorrows, null, null, null);
            Second = new RootBlock(new VolumeKey('T'), Mapped, SecondBorrows, null, null, null);
        }

        internal MappedBlock Mapped { get; }
        internal ViewBorrows FirstBorrows { get; } = new();
        internal ViewBorrows SecondBorrows { get; } = new();
        internal RootBlock First { get; }
        internal RootBlock Second { get; }

        public void Dispose()
        {
            Mapped.ReleaseState.ReleaseUnborrowed();
            _block.Dispose();
        }
    }
}
