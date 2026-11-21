namespace MFTLib.Index;

public sealed partial class FileIndex
{
    /// <summary>
    ///     Reads the resident <c>$DATA</c> of small files straight from their MFT records and lends each answer to
    ///     <paramref name="handler" />. A file whose data fits its record has its bytes inside the MFT, so one read
    ///     of the right records returns many small files without opening any of them. MFTLib returns the bytes and
    ///     does nothing else with them: hashing, parsing and caching belong to the caller.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The entries are mapped against the index's current block under a snapshot borrow, which is returned
    ///         before any I/O: an entry that is deleted or superseded in the index is answered
    ///         <see cref="InlineContentOutcome.Superseded" /> and a directory
    ///         <see cref="InlineContentOutcome.NotInline" />, without reading anything. The rest are sorted by record
    ///         number, neighbours are read together in bounded pieces, and each record is checked before its
    ///         bytes are believed: the FILE signature and update sequence array, the in-use flag, the sequence
    ///         number the entry was minted with, a base record, and a plain resident unnamed <c>$DATA</c> whose
    ///         value lies inside its attribute and record.
    ///     </para>
    ///     <para>
    ///         The records are read on demand from the volume, or from the very dump file the index scanned (even
    ///         after its path was renamed, deleted or replaced, until a successful rescan), so the bytes are as fresh
    ///         as the file system's own metadata. <b>The read does not consult file permissions:</b> a direct raw-volume
    ///         source requires an already elevated caller, and a dump source reads the dump without elevation.
    ///         Broker-sourced indexes currently return <see cref="RootOperationOutcome.NotApplicable" />; the broker
    ///         content operation is future work. See the permissions ruling in docs/api-decisions.md.
    ///     </para>
    ///     <para>
    ///         On <see cref="RootOperationOutcome.Succeeded" /> the handler has been called exactly once for every
    ///         position of <paramref name="entries" />, serially and in unspecified order, duplicates included. When
    ///         any entry needs a record read, no handler call happens before the source has been opened and checked,
    ///         so a source that cannot be read, or cannot be confirmed as the one the index scanned, fails the call with no answer
    ///         delivered. The
    ///         handler runs on a thread-pool thread and must not call back into this index's lifecycle verbs. An
    ///         exception it throws stops the read and propagates unchanged from the returned task, even when a
    ///         cancellation was also requested.
    ///     </para>
    /// </remarks>
    /// <param name="entries">The entries to read, taken from this index.</param>
    /// <param name="handler">Receives one <see cref="InlineContentResult" /> per entry.</param>
    /// <param name="cancellationToken">
    ///     Observed before the entries are checked, every 4096 entries while they are checked and mapped, between the
    ///     batches of records read, inside a read, and before any result is reported, so a cancellation requested before
    ///     that last check, from the handler or elsewhere, ends the call with <see cref="OperationCanceledException" />
    ///     instead of a result. An exception the handler throws takes precedence: a handler that cancels the token and
    ///     then throws gets its own exception back. Index disposal cancels the read as well.
    /// </param>
    /// <returns>
    ///     <see cref="RootOperationOutcome.Succeeded" /> when every entry was answered;
    ///     <see cref="RootOperationOutcome.NotApplicable" /> when the root has no MFT source that can read records
    ///     (a directory-walk root, an index opened without a source, a broker-sourced index) and the handler was not
    ///     called; <see cref="RootOperationOutcome.Failed" /> with the exception when the volume or dump failed
    ///     (some answers may already have been delivered).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="entries" /> or <paramref name="handler" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     An entry is the default <see cref="FileEntry" /> (at call time), or belongs to another
    ///     <see cref="FileIndex" /> or to a root block a rescan replaced (in the returned task).
    /// </exception>
    /// <exception cref="ObjectDisposedException">The index has been disposed.</exception>
    /// <exception cref="OperationCanceledException">
    ///     The token was cancelled (at call time when it already was), or the index was disposed during the read.
    /// </exception>
    public Task<RootOperationResult> ReadInlineContentAsync(IReadOnlyList<FileEntry> entries,
        InlineContentHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(handler);
        for (var position = 0; position < entries.Count; position++)
        {
            InlineContentPlan.ObserveCancellation(position, cancellationToken);
            if (!entries[position].IsValid)
            {
                throw new ArgumentException(
                    $"entries[{position}] is the default FileEntry, which names no file. Take entries from the index being read.",
                    nameof(entries));
            }
        }

        return ReadInlineContentCoreAsync(entries, handler, cancellationToken);
    }

    async Task<RootOperationResult> ReadInlineContentCoreAsync(IReadOnlyList<FileEntry> entries,
        InlineContentHandler handler, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, DisposalToken);
        var effectiveToken = linkedCancellation.Token;
        // Every result is reported only after a last look at the token, so a cancellation requested before that look
        // ends the call with OperationCanceledException instead of the result. A handler exception is rethrown before
        // the token is looked at, so it wins over a cancellation requested in the same call.
        if (PlanInlineContent(entries, effectiveToken) is not { } read)
        {
            effectiveToken.ThrowIfCancellationRequested();
            return new RootOperationResult(RootKey, RootOperationOutcome.NotApplicable, null);
        }

        using var lease = read.Target.Lease;
        read.Plan.SortRequests();
        var delivery = new InlineContentDelivery(handler);
        try
        {
            await Task.Run(() => DeliverInlineContentAsync(read.Target, read.Plan, delivery, effectiveToken), effectiveToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            delivery.RethrowHandlerFailure();
            if (failure is OperationCanceledException)
            {
                throw;
            }

            // A fault the cancellation itself caused (a disposed pipe, a closed handle) is a cancellation too.
            effectiveToken.ThrowIfCancellationRequested();

            return new RootOperationResult(RootKey, RootOperationOutcome.Failed, failure);
        }

        effectiveToken.ThrowIfCancellationRequested();
        return new RootOperationResult(RootKey, RootOperationOutcome.Succeeded, null);
    }

    // Runs on a thread-pool thread, so no handler call happens on the caller's stack. With nothing to read, the answers
    // decided without I/O are delivered at once; otherwise they wait for the reader's first answer, which follows its
    // open and its source check, so a read the reader refuses has called the handler for nothing. Deliveries check the
    // token only every few thousand answers, so a cancellation after the last check is observed here, before the read
    // can report success.
    static async Task DeliverInlineContentAsync(InlineContentTarget target, InlineContentPlan plan,
        InlineContentDelivery delivery, CancellationToken cancellationToken)
    {
        if (plan.Requests.Length == 0)
        {
            delivery.DeliverDecided(plan.Decided, cancellationToken);
        }
        else
        {
            await target.Reader.ReadAsync(target.Volume, target.VolumeSerial, plan.Requests,
                    delivery.SinkAfterDecided(plan.Decided, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    ///     The reader of a root's records, the volume its MFT blocks belong to, and the lease that keeps a dump
    ///     block's file open for the read.
    /// </summary>
    readonly record struct InlineContentTarget(IInlineContentReader Reader, VolumeKey Volume, ulong VolumeSerial,
        IDisposable? Lease);

    // Capture the reader, its lease and its rows from one mapping. No I/O and no sort retains the borrow.
    (InlineContentTarget Target, InlineContentPlan Plan)? PlanInlineContent(IReadOnlyList<FileEntry> entries,
        CancellationToken cancellationToken)
    {
        using var borrow = BorrowCurrentSnapshot();
        if (borrow.Pinned is not { } pinned)
        {
            return entries.Count == 0 ? null : throw new ArgumentException(
                $"The index has no block for root {RootKey}, so entries[0] cannot belong to it.", nameof(entries));
        }

        var reader = pinned.Block.DumpContentReader ?? _options.MftSource?.InlineContentReader;
        if (reader is null || pinned.RootBlock.ProducerKind != ProducerKind.Mft ||
            !pinned.Block.Root.TryGetVolume(out var volume))
        {
            InlineContentPlan.ValidateOwnership(pinned, entries, cancellationToken);
            return null;
        }

        var plan = InlineContentPlan.Create(pinned, entries, cancellationToken);
        // The lease is taken last, so an argument rejected above leaks none.
        return (new InlineContentTarget(reader, volume, pinned.Block.Header.VolumeSerial,
            pinned.Block.DumpContentReader?.Lease()), plan);
    }
}
