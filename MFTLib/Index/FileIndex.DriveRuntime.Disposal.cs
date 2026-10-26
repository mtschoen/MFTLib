namespace MFTLib.Index;

public sealed partial class FileIndex
{
    sealed partial class DriveRuntime
    {
        readonly List<DriveBlockRelease> _retiredBlocks = [];

        /// <summary>
        ///     Keeps a replaced block's release state until its release completes, so disposal can
        ///     release it even after its last handle was collected. The caller holds
        ///     <see cref="StateLock" />.
        /// </summary>
        void RetireBlockLocked(DriveBlock replaced)
        {
            lock (StateLock)
            {
                _retiredBlocks.RemoveAll(retired => retired.IsReleaseComplete);
                _retiredBlocks.Add(replaced.ReleaseState);
            }
        }

        /// <summary>
        ///     Prepares this drive for disposal under <see cref="StateLock" />. Cancels any pending
        ///     restart waiter, clears active recovery, retires the current instance, and gathers
        ///     drains for the retiring instance, current instance, and any in-flight recovery tasks.
        /// </summary>
        public WatchHandoff PrepareDisposalLocked()
        {
            _watchRequested = false;
            _refusedStartFault = null;
            CancelRestartPendingWaiterLocked();
            ClearRecoveryLocked();
            var drains = new List<Task>();
            if (_retiring is { } alreadyRetiring)
            {
                drains.Add(alreadyRetiring.Drained);
            }

            var retired = RetireCurrentLocked();
            if (retired is not null)
            {
                drains.Add(retired.Drained);
            }

            drains.AddRange(_recoveryCompletions);
            NoteWatchStateLocked();
            return new WatchHandoff(retired, Task.WhenAll(drains));
        }

        /// <summary>
        ///     Releases all retired blocks that were replaced during rescans.
        /// </summary>
        public async Task ReleaseRetiredBlocksAsync()
        {
            DriveBlockRelease[] retired;
            lock (StateLock)
            {
                retired = [.. _retiredBlocks];
                _retiredBlocks.Clear();
            }

            foreach (var release in retired)
            {
                await release.DrainAndReleaseAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        ///     Detaches and returns the current block and clears the last result. The caller holds
        ///     <see cref="StateLock" />.
        /// </summary>
        public DriveBlock? DetachCurrentBlockLocked()
        {
            var current = Block;
            Block = null;
            _lastResult = null;
            return current;
        }

        /// <summary>
        ///     Releases any uncommitted block and relinquishes canonical ownership. The caller holds
        ///     <see cref="StateLock" />.
        /// </summary>
        public void ReleaseUnpublishedBlockLocked()
        {
            DetachCurrentBlockLocked()?.ReleaseState.ReleaseUnborrowed();
            ReleaseCanonicalOwnershipLocked();
        }
    }
}
