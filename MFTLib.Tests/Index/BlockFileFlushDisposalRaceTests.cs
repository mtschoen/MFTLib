using MFTLib.Index;
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

        var held = new ReleaseGate();
        var releaseFlush = new ReleaseGate();
        var disposeStarted = new ReleaseGate();
        var holdOnce = 0;
        void HoldFirst()
        {
            if (Interlocked.Exchange(ref holdOnce, 1) == 0)
            {
                held.Set();
                releaseFlush.Wait();
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
            disposeStarted.Set();
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
            Assert.IsTrue(held.Wait(HandoffTimeout), "the flush never reached the hold point, so the race was never set up");

            var disposeTask = Task.Run(() => block.Dispose());
            Assert.IsTrue(disposeStarted.Wait(HandoffTimeout), "dispose never began, so the race was never set up");
            Assert.IsTrue(disposeWillWait,
                "dispose found no in-flight access to wait for, so a flush was not holding the block");

            releaseFlush.Set();

            Assert.IsTrue(flushTask.Wait(HandoffTimeout), "the in-flight flush never finished");
            Assert.IsTrue(disposeTask.Wait(HandoffTimeout), "dispose never finished after the flush left the block");
            Assert.ThrowsException<ObjectDisposedException>(() => block.Flush(null));
        }
        finally
        {
            releaseFlush.Set();
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
