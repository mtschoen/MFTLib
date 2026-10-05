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
    static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(30);

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
        block._flushRangeBytes = Environment.SystemPageSize;

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

        block._flushViewRange = (_, _) =>
        {
            if (holdInsideRangeFlush)
            {
                HoldFirst();
            }

            return 0;
        };
        block._synchronizeViewRange = (_, _, _) =>
        {
            if (holdInsideRangeFlush)
            {
                HoldFirst();
            }

            return 0;
        };
        var disposeWillWait = false;
        block._disposeStartedForTest = willWait =>
        {
            disposeWillWait = willWait;
            disposeStarted.MarkEntered();
        };

        var flushTask = Task.Run(() => block.Flush(_ =>
        {
            if (!holdInsideRangeFlush)
            {
                HoldFirst();
            }
        }));
        try
        {
            Assert.IsTrue(held.Entered.Wait(HandoffTimeout), "the flush never reached the hold point, so the race was never set up");

            var disposeTask = Task.Run(() => block.Dispose());
            Assert.IsTrue(disposeStarted.Entered.Wait(HandoffTimeout), "dispose never began, so the race was never set up");
            Assert.IsTrue(disposeWillWait,
                "dispose found no in-flight access to wait for, so a flush was not holding the block");

            releaseFlush.Release();

            Assert.IsTrue(flushTask.Wait(HandoffTimeout), "the in-flight flush never finished");
            Assert.IsTrue(disposeTask.Wait(HandoffTimeout), "dispose never finished after the flush left the block");
            Assert.ThrowsException<ObjectDisposedException>(() => block.Flush(null));
        }
        finally
        {
            releaseFlush.Release();
        }
    }
}
