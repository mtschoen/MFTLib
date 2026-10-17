namespace MFTLib.Index;

internal sealed partial class BlockFile
{
    internal BlockHooks Hooks { get; }

    /// <summary>Per-block test hooks for disposal, view flushing and retry timing.</summary>
    internal sealed class BlockHooks(BlockFile block)
    {
        internal Action<bool>? DisposeStarted;

        // aislop-ignore-next-line jb/MemberHidesStaticFromOuterClass
        internal long FlushRangeBytes = BlockFile.FlushRangeBytes;
        internal Func<long, long, int> FlushViewRange = block.FlushViewRangeNatively;
        internal Func<long, long, int, int> SynchronizeViewRange = block.SynchronizeViewRangeNatively;
        internal Action<int> PauseMilliseconds = Thread.Sleep;
        internal SpinStep SpinOnce = static (ref spinWait) => spinWait.SpinOnce();
    }
}
