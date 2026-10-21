using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A BlockWriter has no synchronization of its own against BlockFile.Dispose, which is the
///     shape a broker fixture has when the client cancels: the serving task keeps writing rows
///     while teardown disposes the block on another thread. Before disposal was serialized
///     against in-flight writes, the teardown unmapped the view under the writer and the next
///     row access killed the process with an AccessViolationException (git-wizard#181).
/// </summary>
[TestClass]
public class BlockWriterDisposalRaceTests
{
    static readonly DateTime Moment = new(2026, 9, 14, 8, 0, 0, DateTimeKind.Utc);

    static RowColumns FileColumns()
    {
        return new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 32, Size: 4096,
            ModifiedTicks: Moment.Ticks, SequenceNumber: 0);
    }

    [TestMethod]
    public void TryWriteRow_RacingDispose_CompletesTheWriteThenDisposeUnmaps()
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        var writer = new BlockWriter(block);

        var rowCaptured = new TestGate();
        var releaseWriter = new TestGate();
        var disposeStarted = new TestGate();
        writer.Hooks._rowCaptured = () =>
        {
            rowCaptured.MarkEntered();
            releaseWriter.WaitForRelease();
        };
        block.Hooks._disposeStarted = _ => disposeStarted.MarkEntered();

        Task? disposeTask = null;
        var writeTask = Task.Run(() => writer.TryWriteRow(1, "report.pdf", FileColumns()));
        try
        {
            Assert.IsTrue(rowCaptured.Entered.Wait(HangGuard * 3),
                "the writer never reached the row capture, so the race was never set up");

            disposeTask = Task.Run(() => block.Dispose());
            Assert.IsTrue(disposeStarted.Entered.Wait(HangGuard * 3),
                "dispose never began, so the race was never set up");
            Assert.IsFalse(disposeTask.IsCompleted,
                "dispose returned while a writer was still inside the block");

            releaseWriter.Release();

            Assert.IsTrue(writeTask.Wait(HangGuard * 3), "the in-flight write never finished");
            Assert.IsTrue(writeTask.Result,
                "a write admitted before disposal began must complete against the still-mapped block");
            Assert.IsTrue(disposeTask.Wait(HangGuard * 3),
                "dispose never finished after the writer left the block");

            Assert.ThrowsExactly<ObjectDisposedException>(
                () => writer.TryWriteRow(2, "late.pdf", FileColumns()),
                "a write that arrives after disposal must fail with a catchable exception");
        }
        finally
        {
            // A failed assertion must not leave the writer parked on a thread-pool thread.
            releaseWriter.Release();
            writeTask.Wait(HangGuard * 3);
            disposeTask?.Wait(HangGuard * 3);
        }
    }

    [TestMethod]
    public void WriterMembers_AfterDispose_ThrowObjectDisposedExceptionInsteadOfTouchingMemory()
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        var writer = new BlockWriter(block);
        block.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.TryWriteRow(1, "report.pdf", FileColumns()));
        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.TryRenameRow(1, "renamed.pdf", 0));
        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.MarkTombstone(1));
        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.MarkSubtreeSkipped(1));
        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.MarkCapacityExhausted());
        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.SetJournalCursor(1, 2));
        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.SetCacheTag(new CacheTag("TEST", 7)));
        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.BumpGeneration());
        Assert.ThrowsExactly<ObjectDisposedException>(() => writer.Complete(Moment, null));
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = writer.RowCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = writer.CapacityExhausted);
    }
}
