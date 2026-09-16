using System.Diagnostics;
using MFTLib.Index;
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
    static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(30);

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

        var rowCaptured = new ReleaseGate();
        var releaseWriter = new ReleaseGate();
        var disposeStarted = new ReleaseGate();
        writer._rowCapturedForTest = () =>
        {
            rowCaptured.Set();
            releaseWriter.Wait();
        };
        block._disposeStartedForTest = disposeStarted.Set;

        var writeTask = Task.Run(() => writer.TryWriteRow(1, "report.pdf", FileColumns()));
        try
        {
            Assert.IsTrue(rowCaptured.Wait(HandoffTimeout),
                "the writer never reached the row capture, so the race was never set up");

            var disposeTask = Task.Run(() => block.Dispose());
            Assert.IsTrue(disposeStarted.Wait(HandoffTimeout),
                "dispose never began, so the race was never set up");
            Assert.IsFalse(disposeTask.IsCompleted,
                "dispose returned while a writer was still inside the block");

            releaseWriter.Set();

            Assert.IsTrue(writeTask.Wait(HandoffTimeout), "the in-flight write never finished");
            Assert.IsTrue(writeTask.Result,
                "a write admitted before disposal began must complete against the still-mapped block");
            Assert.IsTrue(disposeTask.Wait(HandoffTimeout),
                "dispose never finished after the writer left the block");

            Assert.ThrowsException<ObjectDisposedException>(
                () => writer.TryWriteRow(2, "late.pdf", FileColumns()),
                "a write that arrives after disposal must fail with a catchable exception");
        }
        finally
        {
            // A failed assertion must not leave the writer parked on a thread-pool thread.
            releaseWriter.Set();
        }
    }

    [TestMethod]
    public void WriterMembers_AfterDispose_ThrowObjectDisposedExceptionInsteadOfTouchingMemory()
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        var writer = new BlockWriter(block);
        block.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() => writer.TryWriteRow(1, "report.pdf", FileColumns()));
        Assert.ThrowsException<ObjectDisposedException>(() => writer.TryRenameRow(1, "renamed.pdf", 0));
        Assert.ThrowsException<ObjectDisposedException>(() => writer.MarkTombstone(1));
        Assert.ThrowsException<ObjectDisposedException>(() => writer.MarkSubtreeSkipped(1));
        Assert.ThrowsException<ObjectDisposedException>(() => writer.MarkCompactionNeeded());
        Assert.ThrowsException<ObjectDisposedException>(() => writer.SetJournalCursor(1, 2));
        Assert.ThrowsException<ObjectDisposedException>(() => writer.BumpGeneration());
        Assert.ThrowsException<ObjectDisposedException>(() => writer.Complete(Moment));
        Assert.ThrowsException<ObjectDisposedException>(() => _ = writer.RowCount);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = writer.CompactionNeeded);
    }

    /// <summary>
    ///     A one-shot gate the release seam and the test share across threads. Deliberately not a
    ///     <see cref="ManualResetEventSlim" />: that is disposable, and a disposable captured by
    ///     the seam's closure is exactly what the quality gate refuses.
    /// </summary>
    sealed class ReleaseGate
    {
        readonly object _lock = new();
        bool _open;

        public void Set()
        {
            lock (_lock)
            {
                _open = true;
                Monitor.PulseAll(_lock);
            }
        }

        public void Wait()
        {
            lock (_lock)
            {
                while (!_open)
                {
                    Monitor.Wait(_lock);
                }
            }
        }

        public bool Wait(TimeSpan timeout)
        {
            lock (_lock)
            {
                var remaining = timeout;
                var stopwatch = Stopwatch.StartNew();
                while (!_open)
                {
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(_lock, remaining))
                    {
                        return false;
                    }

                    remaining = timeout - stopwatch.Elapsed;
                }

                return true;
            }
        }
    }
}
