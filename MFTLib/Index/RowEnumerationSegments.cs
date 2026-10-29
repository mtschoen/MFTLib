using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     Shared heap-allocated state for a multi-index <see cref="IndexRowEnumerator" /> over a
///     <see cref="FileIndexSet" />. Borrows at most one member index's block at a time, releasing
///     the current segment's borrow before borrowing the next.
/// </summary>
sealed class RowEnumerationSegments : IDisposable
{
    readonly IReadOnlyList<FileIndex> _indices;
    readonly CancellationToken _callerCancellationToken;
    FileIndex.QueryScope _activeScope;
    bool _hasActiveScope;
    bool _initialConsumed;
    int _nextIndex;
    int _activeSegment;
    int _disposed;

    internal RowEnumerationSegments(IReadOnlyList<FileIndex> indices, CancellationToken callerCancellationToken)
    {
        _indices = indices;
        _callerCancellationToken = callerCancellationToken;
        if (indices.Count > 0)
        {
            _activeScope = indices[0].BeginQuery(callerCancellationToken);
            _hasActiveScope = true;
            _nextIndex = 1;
        }
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal int ActiveSegment => Volatile.Read(ref _activeSegment);

    internal bool TryMoveNextSegment([NotNullWhen(true)] out RootBlock? rootBlock, out uint? admittedViewRow,
        out CancellationToken cancellationToken, ref int enumeratorSegment)
    {
        while (true)
        {
            if (!_initialConsumed)
            {
                _initialConsumed = true;
                if (!_hasActiveScope)
                {
                    _callerCancellationToken.ThrowIfCancellationRequested();
                    rootBlock = null;
                    admittedViewRow = null;
                    cancellationToken = default;
                    return false;
                }
            }
            else
            {
                var wasCancelled = _hasActiveScope && _activeScope.CancellationToken.IsCancellationRequested;
                var cancelledToken = wasCancelled ? _activeScope.CancellationToken : default;
                ReleaseActiveScope();
                if (wasCancelled)
                {
                    throw new OperationCanceledException(cancelledToken);
                }

                if (_nextIndex >= _indices.Count)
                {
                    enumeratorSegment = _activeSegment;
                    _callerCancellationToken.ThrowIfCancellationRequested();
                    rootBlock = null;
                    admittedViewRow = null;
                    cancellationToken = default;
                    return false;
                }

                _callerCancellationToken.ThrowIfCancellationRequested();
                _activeScope = _indices[_nextIndex++].BeginQuery(_callerCancellationToken);
                _hasActiveScope = true;
                enumeratorSegment = _activeSegment;
            }

            _activeScope.CancellationToken.ThrowIfCancellationRequested();
            if (_activeScope.RootBlock is { } block)
            {
                rootBlock = block;
                admittedViewRow = _activeScope.ViewRow;
                cancellationToken = _activeScope.CancellationToken;
                return true;
            }
        }
    }

    void ReleaseActiveScope()
    {
        if (_hasActiveScope)
        {
            Interlocked.Increment(ref _activeSegment);
            _hasActiveScope = false;
            _activeScope.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ReleaseActiveScope();
        }
    }
}
