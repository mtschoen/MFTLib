namespace MFTLib.Index;

/// <summary>
///     What a call of <see cref="FileIndex.ReadInlineContentAsync" /> has to do once its entries are mapped against
///     the block it borrowed: the entries decided without I/O, and the requests that need the volume. Built under the
///     borrow and used after it is returned, so it holds no mapped memory; the requests are sorted after the borrow is
///     returned, by <see cref="SortRequests" />.
/// </summary>
/// <param name="Requests">The entries that need a record read, ascending by record number once sorted.</param>
/// <param name="Decided">The entries whose answer the index already knew, with that answer.</param>
sealed record InlineContentPlan(InlineContentRequest[] Requests, (int Position, InlineContentOutcome Outcome)[] Decided)
{
    /// <summary>The entries walked between two observations of the caller's token.</summary>
    internal const int CancellationCheckInterval = 4096;

    /// <summary>
    ///     Maps each entry to a request, or decides it from the borrowed block: a deleted or superseded entry is
    ///     <see cref="InlineContentOutcome.Superseded" />, a directory <see cref="InlineContentOutcome.NotInline" />.
    ///     A row index is the MFT record number of an MFT block, including in a directory view of the volume's block.
    /// </summary>
    /// <exception cref="ArgumentException">An entry does not belong to <paramref name="pinned" />'s root block.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; observed every <see cref="CancellationCheckInterval" /> entries.</exception>
    internal static InlineContentPlan Create(PinnedRootBlock pinned, IReadOnlyList<FileEntry> entries,
        CancellationToken cancellationToken)
    {
        var requests = new List<InlineContentRequest>(entries.Count);
        var decided = new List<(int Position, InlineContentOutcome Outcome)>();
        for (var position = 0; position < entries.Count; position++)
        {
            ObserveCancellation(position, cancellationToken);
            var entry = RequireOwnedEntry(pinned, entries, position);
            if (entry.IsSupersededIn(pinned) || pinned.Block.Rows[(int)entry.RowIndex].IsDeleted)
            {
                decided.Add((position, InlineContentOutcome.Superseded));
            }
            else if (pinned.Block.Rows[(int)entry.RowIndex].IsDirectory)
            {
                decided.Add((position, InlineContentOutcome.NotInline));
            }
            else
            {
                requests.Add(new InlineContentRequest(position, entry.RowIndex, entry.SequenceNumber));
            }
        }

        return new InlineContentPlan(requests.ToArray(), decided.ToArray());
    }

    /// <summary>Puts <see cref="Requests" /> in ascending record order, the order the readers require.</summary>
    internal void SortRequests()
    {
        Array.Sort(Requests, static (left, right) => left.RecordNumber != right.RecordNumber
            ? left.RecordNumber.CompareTo(right.RecordNumber)
            : left.Position.CompareTo(right.Position));
    }

    internal static void ValidateOwnership(PinnedRootBlock pinned, IReadOnlyList<FileEntry> entries,
        CancellationToken cancellationToken)
    {
        for (var position = 0; position < entries.Count; position++)
        {
            ObserveCancellation(position, cancellationToken);
            RequireOwnedEntry(pinned, entries, position);
        }
    }

    /// <summary>Observes the token at the first position and then every <see cref="CancellationCheckInterval" /> positions.</summary>
    internal static void ObserveCancellation(int position, CancellationToken cancellationToken)
    {
        if (position % CancellationCheckInterval == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    static FileEntry RequireOwnedEntry(PinnedRootBlock pinned, IReadOnlyList<FileEntry> entries, int position)
    {
        var entry = entries[position];
        return ReferenceEquals(entry.NamedRootBlock, pinned.RootBlock)
            ? entry
            : throw ForeignEntry(position, pinned.RootBlock, nameof(entries));
    }

    // A block that is not the read index's current block is foreign whether or not its own index is still alive:
    // ObjectDisposedException is reserved for disposal of the index being read.
    static ArgumentException ForeignEntry(int position, RootBlock current, string parameterName)
    {
        return new ArgumentException(
            $"entries[{position}] does not belong to the read index's current block for root {current.Root}: it came " +
            "from another index, another view of the same volume, or a block a rescan replaced. Take new entries " +
            "from the index being read.", parameterName);
    }
}
