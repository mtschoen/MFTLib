namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     One queued recovery of a root (spec 2.6.5): the faulted instance and the block it was
    ///     armed from, which the recovery revalidates once it holds the root's lifecycle gate, and
    ///     a cancellation source that the index's disposal cancels. Its completion never faults and
    ///     completes its continuations asynchronously, so no waiter's code runs on the recovery's
    ///     stack.
    /// </summary>
    internal sealed class RecoveryTicket
    {
        readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly CancellationTokenRegistration _disposalLink;

        public RecoveryTicket(WatchInstance failedInstance, RootBlock failedBlock, CancellationToken disposalToken)
        {
            FailedInstance = failedInstance;
            FailedBlock = failedBlock;

            // Linked by hand, as WatchInstance links its start: the link is removed when the
            // recovery ends without disposing the source, which disposal may still be cancelling.
            _disposalLink = disposalToken.UnsafeRegister(
                static state => (state as CancellationTokenSource)?.Cancel(), Cancellation);
        }

        public WatchInstance FailedInstance { get; }

        /// <summary>
        ///     The root block this recovery owns: the one its instance was armed from, replaced by
        ///     each block the recovery's own scans publish, so revalidation tells its own
        ///     publication from external activity. Written under the state lock.
        /// </summary>
        public RootBlock FailedBlock { get; set; }

        /// <summary>Cancelled by the index's disposal; never disposed.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        public Task Completion => _completion.Task;

        readonly TaskCompletionSource _superseded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when a stop, start, rescan or disposal clears this ticket from its root.</summary>
        public Task Superseded => _superseded.Task;

        public void MarkSuperseded() => _superseded.TrySetResult();

        /// <summary>Whether the latest rescan attempt of this recovery failed in the producer, publishing nothing.</summary>
        public bool RescanProducerFailed { get; set; }

        public void Complete()
        {
            _disposalLink.Dispose();
            _completion.TrySetResult();
        }
    }
}
