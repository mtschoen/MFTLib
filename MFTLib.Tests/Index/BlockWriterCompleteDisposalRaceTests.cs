using MFTLib.Index;
using MFTLib.Tests.TestSupport;
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

        var accessTaken = new TestGate();
        var releaseComplete = new TestGate();
        var disposeStarted = new TestGate();
        var disposeWillWait = false;
        writer._completeAccessTakenForTest = () =>
        {
            accessTaken.MarkEntered();
            releaseComplete.WaitForRelease();
        };
        block._disposeStartedForTest = willWait =>
        {
            disposeWillWait = willWait;
            disposeStarted.MarkEntered();
        };

        var completeTask = Task.Run(() => writer.Complete(Moment, null));
        try
        {
            Assert.IsTrue(accessTaken.Entered.Wait(HandoffTimeout),
                "Complete never took its access, so the race was never set up");

            var disposeTask = Task.Run(() => block.Dispose());
            Assert.IsTrue(disposeStarted.Entered.Wait(HandoffTimeout), "dispose never began, so the race was never set up");
            Assert.IsTrue(disposeWillWait,
                "dispose found no in-flight access to wait for, so Complete was not holding the block");

            releaseComplete.Release();

            completeTask.Wait(HandoffTimeout);
            Assert.AreEqual(TaskStatus.RanToCompletion, completeTask.Status,
                "Complete was admitted before disposal began and must finish its flush without throwing");
            Assert.IsTrue(disposeTask.Wait(HandoffTimeout), "dispose never finished after Complete left the block");
        }
        finally
        {
            releaseComplete.Release();
        }
    }
}
