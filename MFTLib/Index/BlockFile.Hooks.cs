namespace MFTLib.Index;

internal sealed partial class BlockFile
{
    internal BlockHooks Hooks { get; }

    /// <summary>Per-block test hooks for disposal, view flushing and retry timing.</summary>
    internal sealed class BlockHooks(BlockFile block)
    {
        internal Action<bool>? DisposeStarted;

        /// <summary>
        ///     Observes the absolute row index immediately before an existing scan cancellation check.
        ///     Configured before scanning, called outside ownership locks, immediately before a token check,
        ///     potentially concurrently by Search partitions. Callbacks must be nonblocking, nonthrowing, and
        ///     synchronize their mutable state when several partitions run.
        /// </summary>
        internal Action<uint>? RowScanCheckpoint;

        // aislop-ignore-next-line jb/MemberHidesStaticFromOuterClass
        internal long FlushRangeBytes = BlockFile.FlushRangeBytes;
        internal Func<long, long, int> FlushViewRange = block.FlushViewRangeNatively;
        internal Func<long, long, int, int> SynchronizeViewRange = block.SynchronizeViewRangeNatively;
        internal Action<int> PauseMilliseconds = Thread.Sleep;
        internal SpinStep SpinOnce = static (ref spinWait) => spinWait.SpinOnce();
    }
}
