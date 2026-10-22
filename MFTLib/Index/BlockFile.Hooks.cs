namespace MFTLib.Index;

sealed partial class BlockFile
{
    internal BlockHooks Hooks { get; }

    /// <summary>Per-block test hooks for disposal, view flushing and retry timing.</summary>
    internal sealed class BlockHooks(BlockFile block)
    {
        internal Action<bool>? _disposeStarted;

        internal long _flushRangeBytes = FlushRangeBytes;
        internal Func<long, long, int> _flushViewRange = block.FlushViewRangeNatively;
        internal Func<long, long, int, int> _synchronizeViewRange = block.SynchronizeViewRangeNatively;
        internal Action<int> _pauseMilliseconds = Thread.Sleep;
        internal SpinStep _spinOnce = static (ref spinWait) => spinWait.SpinOnce();
    }
}
