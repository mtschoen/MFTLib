using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A public <see cref="BlockFile.Flush" /> holds the view for its whole duration, so a racing
///     <see cref="BlockFile.Dispose" /> waits for it rather than unmapping between two ranges.
///     Both platform flush seams are replaced, so the test drives whichever one the host uses.
/// </summary>
[TestClass]
public class BlockFileFlushDisposalRaceTests
{
    [TestMethod]
    public void Flush_DisposeBeginsInsideARangeFlush_DisposeWaitsForTheFlush()
    {
        RunRace(holdInsideRangeFlush: true);
    }

    [TestMethod]
    public void Flush_DisposeBeginsInsideTheRangeCallback_DisposeWaitsForTheFlush()
    {
        RunRace(holdInsideRangeFlush: false);
    }

    static void RunRace(bool holdInsideRangeFlush)
    {
        using var builder = new SyntheticBlockBuilder();
        var block = builder.OpenForWriting();
        block.Hooks._flushRangeBytes = Environment.SystemPageSize;

        var held = new TestGate();
        var releaseFlush = new TestGate();
        var disposeStarted = new TestGate();
        var holdOnce = 0;
        void HoldFirst()
        {
            if (Interlocked.Exchange(ref holdOnce, 1) == 0)
            {
                held.MarkEntered();
                releaseFlush.WaitForRelease();
            }
        }

        block.Hooks._flushViewRange = (_, _) =>
        {
            if (holdInsideRangeFlush)
            {
                HoldFirst();
            }

            return 0;
        };
        block.Hooks._synchronizeViewRange = (_, _, _) =>
        {
            if (holdInsideRangeFlush)
            {
                HoldFirst();
            }

            return 0;
        };
        var disposeWillWait = false;
        block.Hooks._disposeStarted = willWait =>
        {
            disposeWillWait = willWait;
            disposeStarted.MarkEntered();
        };

        Task? disposeTask = null;
        var flushTask = Task.Run(() => block.Flush(_ =>
        {
            if (!holdInsideRangeFlush)
            {
                HoldFirst();
            }
        }));
        try
        {
            Assert.IsTrue(held.Entered.Wait(HangGuard * 3), "the flush never reached the hold point, so the race was never set up");

            disposeTask = Task.Run(() => block.Dispose());
            Assert.IsTrue(disposeStarted.Entered.Wait(HangGuard * 3), "dispose never began, so the race was never set up");
            Assert.IsTrue(disposeWillWait,
                "dispose found no in-flight access to wait for, so a flush was not holding the block");

            releaseFlush.Release();

            Assert.IsTrue(flushTask.Wait(HangGuard * 3), "the in-flight flush never finished");
            Assert.IsTrue(disposeTask.Wait(HangGuard * 3), "dispose never finished after the flush left the block");
            Assert.ThrowsExactly<ObjectDisposedException>(() => block.Flush(null));
        }
        finally
        {
            releaseFlush.Release();
            flushTask.Wait(HangGuard * 3);
            disposeTask?.Wait(HangGuard * 3);
        }
    }
}
