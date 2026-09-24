namespace MFTLib.Index;

/// <summary>
///     The readiness the default
///     <see cref="IIndexWatchSource.StartWatching(IReadOnlyList{IndexWatchTarget}, Action, CancellationToken)" />
///     gives a source that reports none: ready once the first
///     <see cref="IAsyncEnumerator{T}.MoveNextAsync" /> call on the stream has returned control
///     with the stream still running. That is either a call still pending, which for an async
///     iterator means it has run its code up to its first incomplete await, or a call that has
///     already produced an item, since a source cannot yield without a running stream. A first
///     call that has already ended the stream or faulted reports nothing, and neither does one
///     that throws, which leaves the pump to fail the start with that end or fault.
///     <para>
///         A pending first call that later ends the stream or faults cannot be taken back: the
///         readiness it reported has already completed the start, so that end or fault reaches the
///         consumer as a failure of a running session instead. That is the limit of a source that
///         reports no readiness of its own.
///     </para>
/// </summary>
sealed class ReadyOnFirstMoveWatchStream(IAsyncEnumerable<WatchStreamItem> inner, Action reportStreamReady)
    : IAsyncEnumerable<WatchStreamItem>
{
    public IAsyncEnumerator<WatchStreamItem> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        return new Enumerator(inner.GetAsyncEnumerator(cancellationToken), reportStreamReady);
    }

    sealed class Enumerator(IAsyncEnumerator<WatchStreamItem> inner, Action reportStreamReady)
        : IAsyncEnumerator<WatchStreamItem>
    {
        bool _firstMoveSeen;

        public WatchStreamItem Current => inner.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            var move = inner.MoveNextAsync();
            if (_firstMoveSeen)
            {
                return move;
            }

            _firstMoveSeen = true;
            if (!move.IsCompleted)
            {
                reportStreamReady();
                return move;
            }

            if (!move.IsCompletedSuccessfully)
            {
                // Faulted or cancelled: returned unobserved so the pump observes it exactly once.
                return move;
            }

            // A ValueTask may back onto a reusable source that allows one read, so the result is
            // read once here and handed on as a fresh value rather than returning the original.
            var produced = move.Result;
            if (produced)
            {
                reportStreamReady();
            }

            return new ValueTask<bool>(produced);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
