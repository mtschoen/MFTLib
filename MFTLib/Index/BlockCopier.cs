namespace MFTLib.Index;

/// <summary>
///     Copies the used regions of one mapped block into an empty, larger mapped block with span copies and no
///     managed staging: rows and sequence numbers, name entries, and name-pool bytes. Posting regions are never
///     copied; the caller chooses whether a fresh posting base is built. Each phase is a separate member so a
///     caller that must observe cancellation between phases, or a later copy that drops unreferenced names,
///     can compose them. Name identifiers are preserved, so no row changes.
/// </summary>
static class BlockCopier
{
    /// <summary>
    ///     Copies <paramref name="source" /> into <paramref name="target" /> and carries the header fields that
    ///     describe the data. The generation is not bumped: the rows are identical.
    /// </summary>
    /// <param name="source">The block to copy from; the caller keeps it unchanged until the copy returns.</param>
    /// <param name="target">An empty block with at least the source's used extents in every region.</param>
    /// <param name="buildPostings">True builds a fresh posting base from the copied rows; false leaves the target unindexed.</param>
    /// <param name="complete">
    ///     True sets the complete flag last, which requires <paramref name="buildPostings" />; false leaves the target
    ///     incomplete, as a block still being filled must stay.
    /// </param>
    /// <param name="cancellationToken">Observed between phases and, with a posting build, every 4096 rows.</param>
    /// <exception cref="ArgumentException">
    ///     The blocks are the same object, the target is not empty or too small, or completion is requested without a
    ///     posting build.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancelled; the target is left incomplete for the caller to discard.</exception>
    internal static void Copy(BlockFile source, BlockFile target, bool buildPostings, bool complete,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (complete && !buildPostings)
        {
            throw new ArgumentException("A completed block needs its posting base.", nameof(complete));
        }

        using var sourceAccess = source.TakeAccess();
        using var targetAccess = target.TakeAccess();
        EnsureFits(source, target);
        cancellationToken.ThrowIfCancellationRequested();
        CopyRows(source, target);
        cancellationToken.ThrowIfCancellationRequested();
        CopyNames(source, target);
        cancellationToken.ThrowIfCancellationRequested();
        CarryHeader(source, target);
        if (buildPostings)
        {
            PostingBuilder.Build(target, null, cancellationToken);
        }

        if (complete)
        {
            target.Header.Flags |= BlockFlags.Complete;
        }
    }

    /// <summary>Checks that the target is a different, empty block with room for everything the source has used.</summary>
    internal static void EnsureFits(BlockFile source, BlockFile target)
    {
        if (ReferenceEquals(source, target))
        {
            throw new ArgumentException("A block cannot be copied into itself.", nameof(target));
        }

        if (target.RowCount != 0 || target.NamePoolUsed != 0 || target.NameEntryCount != 0)
        {
            throw new ArgumentException("A block is copied only into an empty block.", nameof(target));
        }

        if (target.SlotCapacity < source.RowCount || target.NamePoolCapacity < source.NamePoolUsed ||
            target.NameEntryCapacity < source.NameEntryCount)
        {
            throw new ArgumentException("The target block is smaller than the source's used regions.", nameof(target));
        }
    }

    /// <summary>Copies the used rows and their sequence numbers, without publishing the row count.</summary>
    internal static void CopyRows(BlockFile source, BlockFile target)
    {
        var rowCount = (int)source.RowCount;
        source.Rows[..rowCount].CopyTo(target.Rows);
        source.SequenceNumbers[..rowCount].CopyTo(target.SequenceNumbers);
    }

    /// <summary>
    ///     Copies name entries and pool bytes, publishes their counts, and gives the target chains: verbatim when
    ///     the bucket geometry is the same, otherwise rewritten for the target's bucket count.
    /// </summary>
    internal static void CopyNames(BlockFile source, BlockFile target)
    {
        var entryCount = checked((int)source.NameEntryCount);
        var poolUsed = source.NamePoolUsed;
        source.NameEntries[..(entryCount + 1)].CopyTo(target.NameEntries);
        source.GetNamePoolBytes(0, checked((int)poolUsed)).CopyTo(target.GetNamePoolBytes(0, checked((int)poolUsed)));
        target.PublishNamePoolUsed(poolUsed);
        target.PublishNameEntryCount((uint)entryCount);
        if (source.NameEntryCapacity == target.NameEntryCapacity)
        {
            source.Buckets.CopyTo(target.Buckets);
        }
        else
        {
            NamePool.RebuildBuckets(target);
        }
    }

    /// <summary>
    ///     Carries the identity, journal cursor, counts and flags a reader or a watch depends on from the source
    ///     header, and publishes the row count last among the cursors. The capacity-exhausted bit is carried only
    ///     when the source had it; the complete flag is never carried.
    /// </summary>
    internal static void CarryHeader(BlockFile source, BlockFile target)
    {
        var from = source.Header;
        ref var to = ref target.Header;
        to.ProducerKind = from.ProducerKind;
        to.VolumeSerial = from.VolumeSerial;
        to.RootRow = from.RootRow;
        to.ScanTimestampTicks = from.ScanTimestampTicks;
        to.UsnJournalIdentifier = from.UsnJournalIdentifier;
        to.UsnNextUsn = from.UsnNextUsn;
        to.LiveRowCount = from.LiveRowCount;
        to.CacheTagFourCc = from.CacheTagFourCc;
        to.CacheTagVersion = from.CacheTagVersion;
        to.ScanShape = from.ScanShape;
        to.AccessDeniedSubtreeCount = from.AccessDeniedSubtreeCount;
        if (from.IsCapacityExhausted)
        {
            to.Flags |= BlockFlags.CapacityExhausted;
        }

        target.PublishGeneration(source.Generation);
        target.PublishRowCount(source.RowCount);
    }
}
