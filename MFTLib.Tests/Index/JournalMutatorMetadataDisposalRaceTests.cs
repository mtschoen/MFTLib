using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.JournalMutatorDeduplicationTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     Journal row metadata writes hold the block's access scope like every other writer member, so a dispose that
///     begins mid-write waits for the write instead of unmapping the view under it.
/// </summary>
[TestClass]
public class JournalMutatorMetadataDisposalRaceTests
{
    [TestMethod]
    public void RowMetadataUpdate_RacingDispose_CompletesBeforeTheViewIsUnmapped()
    {
        using var builder = SyntheticBlockBuilder.MftShaped();
        var block = builder.OpenForWriting();
        DriveBlock[] snapshot = [new DriveBlock(builder.Drive, 0, block,
            rootDirectoryPath: TestDriveRoot.For(builder.Drive))];
        var writer = new BlockWriter(block);
        var mutator = new JournalMutator(writer);
        var rowCaptured = new TestGate();
        var releaseWriter = new TestGate();
        var disposeStarted = new TestGate();
        writer._rowCapturedForTest = () =>
        {
            rowCaptured.MarkEntered();
            releaseWriter.WaitForRelease();
        };
        var disposeWillWait = false;
        block._disposeStartedForTest = willWait =>
        {
            disposeWillWait = willWait;
            disposeStarted.MarkEntered();
        };

        var timestamp = new DateTime(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);
        var applyTask = Task.Run(() => mutator.Apply(snapshot[0],
            [CreateEntry(7, 6, "notes.txt", UsnReason.DataExtend, timestamp)], 7, 2000));
        try
        {
            Assert.IsTrue(rowCaptured.Entered.Wait(HangGuard * 3),
                "the metadata write never ran inside the writer's access scope, so the race was never set up");
            var disposeTask = Task.Run(block.Dispose);
            Assert.IsTrue(disposeStarted.Entered.Wait(HangGuard * 3), "dispose never began");
            Assert.IsTrue(disposeWillWait, "dispose did not see the metadata write's access scope, so it would unmap under it");
            releaseWriter.Release();

            // Later steps of the same batch may arrive after disposal began and fail with the catchable exception;
            // the write that was already inside the block is what must have completed first.
            try
            {
                Assert.IsTrue(applyTask.Wait(HangGuard * 3), "the in-flight apply never finished");
            }
            catch (AggregateException exception) when (exception.InnerException is ObjectDisposedException)
            {
                // Expected for a step admitted after disposal began.
            }

            Assert.IsTrue(disposeTask.Wait(HangGuard * 3), "dispose never finished");
        }
        finally
        {
            releaseWriter.Release();
            snapshot.ReleaseNowAsync().Wait(HangGuard * 3);
        }
    }
}
