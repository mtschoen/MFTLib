using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A <see cref="BlockWriter.Complete" /> admitted before disposal began finishes its flush
///     even when <see cref="BlockFile.Dispose" /> begins between the writer's access and the flush:
///     the flush runs under the access Complete already holds instead of asking for a second one.
/// </summary>
[TestClass]
public class BlockWriterCompleteDisposalRaceTests
{
    static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(30);

    static readonly DateTime Moment = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Complete_DisposeBeginsAfterItsAccess_FinishesTheFlushThenDisposeUnmaps()
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        var writer = new BlockWriter(block);

        var accessTaken = new ReleaseGate();
        var releaseComplete = new ReleaseGate();
        var disposeStarted = new ReleaseGate();
        var disposeWillWait = false;
        writer._completeAccessTakenForTest = () =>
        {
            accessTaken.Set();
            releaseComplete.Wait();
        };
        block._disposeStartedForTest = willWait =>
        {
            disposeWillWait = willWait;
            disposeStarted.Set();
        };

        var completeTask = Task.Run(() => writer.Complete(Moment, null));
        try
        {
            Assert.IsTrue(accessTaken.Wait(HandoffTimeout),
                "Complete never took its access, so the race was never set up");

            var disposeTask = Task.Run(() => block.Dispose());
            Assert.IsTrue(disposeStarted.Wait(HandoffTimeout), "dispose never began, so the race was never set up");
            Assert.IsTrue(disposeWillWait,
                "dispose found no in-flight access to wait for, so Complete was not holding the block");

            releaseComplete.Set();

            completeTask.Wait(HandoffTimeout);
            Assert.AreEqual(TaskStatus.RanToCompletion, completeTask.Status,
                "Complete was admitted before disposal began and must finish its flush without throwing");
            Assert.IsTrue(disposeTask.Wait(HandoffTimeout), "dispose never finished after Complete left the block");
        }
        finally
        {
            releaseComplete.Set();
        }
    }

    /// <summary>A one-shot gate shared across threads; not a disposable, which the quality gate refuses in a closure.</summary>
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
                return _open || (Monitor.Wait(_lock, timeout) && _open);
            }
        }
    }
}
