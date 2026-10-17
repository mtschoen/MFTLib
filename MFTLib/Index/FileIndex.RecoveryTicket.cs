namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     One queued recovery of a drive (spec 2.6.5): the faulted instance and the block it was
    ///     armed from, which the recovery revalidates once it holds the drive's lifecycle gate, and
    ///     a cancellation source that the index's disposal cancels. Its completion never faults and
    ///     completes its continuations asynchronously, so no waiter's code runs on the recovery's
    ///     stack.
    /// </summary>
    sealed class RecoveryTicket
    {
        readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly CancellationTokenRegistration _disposalLink;

        public RecoveryTicket(WatchInstance failedInstance, DriveBlock failedBlock, CancellationToken disposalToken)
        {
            FailedInstance = failedInstance;
            FailedBlock = failedBlock;

            // Linked by hand, as WatchInstance links its start: the link is removed when the
            // recovery ends without disposing the source, which disposal may still be cancelling.
            _disposalLink = disposalToken.UnsafeRegister(
                static state => (state as CancellationTokenSource)?.Cancel(), Cancellation);
        }

        public WatchInstance FailedInstance { get; }

        public DriveBlock FailedBlock { get; }

        /// <summary>Cancelled by the index's disposal; never disposed.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        public Task Completion => _completion.Task;

        public void Complete()
        {
            _disposalLink.Dispose();
            _completion.TrySetResult();
        }
    }
}
