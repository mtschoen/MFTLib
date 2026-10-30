namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     One start of one root's watch, with its own identity: the object reference, plus
    ///     <see cref="Generation" /> for diagnostics. Every effect its pump has is conditioned on
    ///     the instance still being its root's <see cref="VolumeScan.Current" />, so a retiring
    ///     pump never touches its successor's block or state.
    /// </summary>
    internal sealed class WatchInstance
    {
        readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly CancellationTokenRegistration _disposalLink;

        public WatchInstance(RootKey root, long generation, RootBlock armedBlock,
            TaskCompletionSource? catchUpWaiter, CancellationToken disposalToken)
        {
            Root = root;
            Generation = generation;
            ArmedBlock = armedBlock;
            CatchUp = new WatchCatchUpSlot(catchUpWaiter);

            // Linked by hand rather than through CreateLinkedTokenSource, so the link can be
            // removed when the instance drains without disposing the source: a stop that decided
            // to cancel this instance under the state lock cancels it after releasing that lock,
            // and a disposed source would answer it with an exception.
            _disposalLink = disposalToken.UnsafeRegister(
                static state => (state as CancellationTokenSource)?.Cancel(), StartCancellation);
        }

        public RootKey Root { get; }

        public long Generation { get; }

        /// <summary>The block the watch was started from. A batch applies only while it is still published.</summary>
        public RootBlock ArmedBlock { get; }

        /// <summary>Cancels the source's start. Cancelled by the index's disposal as well.</summary>
        public CancellationTokenSource StartCancellation { get; } = new();

        /// <summary>Ends the pump's read, which is how a stop ends a running watch.</summary>
        public CancellationTokenSource PumpStop { get; } = new();

        public WatchInstanceState State = WatchInstanceState.Starting;

        public HashSet<IndexView> SubscriberFaultAnnounced { get; } = [];

        /// <summary>Rethrown once by the stop that retires this instance.</summary>

        public WatchCatchUpSlot CatchUp { get; }

        /// <summary>
        ///     Completes when teardown has finished: the source's start has returned, any handle
        ///     is disposed, and the pump has returned. It never faults.
        /// </summary>
        public Task Drained => _drained.Task;

        /// <summary>
        ///     Cancels the start and the pump. Neither source is ever disposed, so this is safe
        ///     from any thread at any point in the instance's life. Called outside the state lock,
        ///     because cancelling runs the source's callbacks.
        /// </summary>
        public void RequestStop()
        {
            StartCancellation.Cancel();
            PumpStop.Cancel();
        }

        public void CompleteDrain()
        {
            _disposalLink.Dispose();
            _drained.TrySetResult();
        }
    }
}
