namespace MFTLib.Index;

public sealed partial class FileIndex
{
    internal sealed partial class VolumeScan
    {
        readonly List<RootBlockRelease> _retiredBlocks = [];

        /// <summary>
        ///     Withdraws <paramref name="view" />'s watch request. A cancelled start passes the
        ///     <paramref name="request" /> it admitted and withdraws only that one: a newer request on
        ///     the view, and its outstanding fault, are left alone. Disposal passes none.
        /// </summary>
        internal Task WithdrawViewForDisposalAsync(IndexView view, WatchRequest? request = null)
        {
            WatchHandoff handoff;
            lock (StateLock)
            {
                if (request is not null && view.Request is not null && !ReferenceEquals(view.Request, request))
                {
                    return Task.CompletedTask;
                }
                var removed = _requesters.Remove(view);
                view.Request = null;
                _ = TakeOutstandingFaultLocked(view);
                if (!removed)
                {
                    return view.WithdrawalDrain ?? Task.CompletedTask;
                }
                if (AnyRequestLocked)
                {
                    view.WithdrawalDrain = null;
                    return Task.CompletedTask;
                }
                handoff = PrepareDisposalLocked();
                view.WithdrawalDrain = handoff.Drained;
            }
            RaiseWatchStateChanged();
            return handoff.DrainAsync();
        }

        /// <summary>
        ///     Keeps a replaced block's release state until its release completes, so disposal can
        ///     release it even after its last handle was collected. The caller holds
        ///     <see cref="StateLock" />.
        /// </summary>
        void RetireBlockLocked(RootBlock replaced)
        {
            lock (StateLock)
            {
                _retiredBlocks.RemoveAll(retired => retired.IsReleaseComplete);
                _retiredBlocks.Add(replaced.ReleaseState);
            }
        }

        /// <summary>
        ///     Prepares this root for disposal under <see cref="StateLock" />. Cancels any pending
        ///     restart waiter, clears active recovery, retires the current instance, and gathers
        ///     drains for the retiring instance, current instance, and any in-flight recovery tasks.
        /// </summary>
        public WatchHandoff PrepareDisposalLocked()
        {
            AbandonConsumerStartLocked();
            ClearRequestsLocked();
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
            RootBlockRelease[] retired;
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
        public RootBlock? DetachCurrentBlockLocked()
        {
            var current = Block;
            Block = null;
            foreach (var view in _views)
            {
                view.Block = null;
            }
            _lastResult = null;
            return current;
        }

    }
}
