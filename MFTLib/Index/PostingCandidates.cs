using System.Buffers;

namespace MFTLib.Index;

/// <summary>
///     The candidate rows of an exact name query, gathered from the posting regions instead of a row pass: the base
///     list of each matching name identifier plus the journal's delta entries for it, sorted ascending and de-duplicated.
///     A posting is a hint. The caller reads each candidate row's descriptor once and accepts it only if it still names
///     one of the identifiers, so postings left behind by a rename, delete or slot reuse are skipped and a row that
///     stayed unchanged during the query is never missed. See <c>docs/index-format.md</c>.
/// </summary>
/// <remarks>
///     The pass captures the delta count, base counts and row count once, under the caller's borrow, and ignores delta
///     entries and rows beyond them, as a name query ignores identifiers beyond its captured entry count. The
///     identifier zero, the empty name, has no postings (the build skips unnamed rows), so a query for it reads the
///     descriptors once instead; that is the only row pass an exact query makes.
/// </remarks>
sealed class PostingCandidates : IDisposable
{
    /// <summary>Cancellation is observed at least once per this many posting entries visited and candidate rows verified.</summary>
    internal const uint CancellationCheckInterval = RowScanner.CancellationCheckIntervalRows;

    /// <summary>Candidate rows above which a materialized search verifies them on several threads.</summary>
    internal const int ParallelVerificationThreshold = 64 * 1024;

    readonly BlockFile _block;
    readonly CancellationToken _cancellationToken;
    readonly Action? _checkpointObserver;
    readonly uint _deltaCount;
    readonly uint _baseNameCount;
    readonly uint _baseRowCount;
    uint[] _rows = [];
    int _count;

    PostingCandidates(BlockFile block, Action? checkpointObserver, CancellationToken cancellationToken)
    {
        _block = block;
        _cancellationToken = cancellationToken;
        _checkpointObserver = checkpointObserver;

        // The delta count first: a journal append publishes its count before its head, so every entry up to this count
        // is reachable from a head read afterwards, or its row has not been stored yet and is changing under the query.
        _deltaCount = block.DeltaEntryCount;
        _baseNameCount = block.PostingBaseNameCount;
        _baseRowCount = block.PostingBaseRowCount;
        RowCount = block.RowCount;
    }

    /// <summary>The row count captured with the pass; rows at or beyond it are outside the query.</summary>
    internal uint RowCount { get; }

    /// <summary>Candidate rows, ascending and unique.</summary>
    internal int Count => _count;

    /// <summary>The candidate rows, ascending and unique; valid until the pass is disposed.</summary>
    internal ArraySegment<uint> Rows => new(_rows, 0, _count);

    /// <summary>A cadence for the caller's verification loop, observing the token and observer this pass was built with.</summary>
    internal CandidateCheckpoint BeginVerification() => new(_checkpointObserver, _cancellationToken);

    /// <summary>
    ///     Gathers the candidate rows of <paramref name="nameIds" />: identifiers a name pass matched, already bounded by
    ///     its captured entry count.
    /// </summary>
    /// <exception cref="InvalidOperationException">The block holds rows but its postings were never built.</exception>
    /// <exception cref="InvalidDataException">A posting offset or base row lies outside its region.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    internal static PostingCandidates Gather(BlockFile block, ReadOnlySpan<uint> nameIds,
        CancellationToken cancellationToken, Action? checkpointObserver = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        // A block that holds no rows has nothing to post, so it needs no base; one with rows and no base would answer
        // empty and wrong, which is a caller bug worth failing loudly.
        if (!block.PostingsBuilt && block.RowCount > 0)
        {
            throw new InvalidOperationException(
                $"Block file '{block.Path}' has no posting base; an exact name query needs a completed block.");
        }

        var candidates = new PostingCandidates(block, checkpointObserver, cancellationToken);
        try
        {
            candidates.Collect(nameIds);
            return candidates;
        }
        catch
        {
            candidates.Dispose();
            throw;
        }
    }

    /// <summary>The identifiers an exact name pass matched, for <see cref="Gather" />.</summary>
    internal static uint[] IdentifiersOf(NameQueryMatches names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var identifiers = new uint[names.CandidateCount];
        for (var index = 0; index < identifiers.Length; index++)
        {
            identifiers[index] = names.CandidateAt(index);
        }

        return identifiers;
    }

    void Collect(ReadOnlySpan<uint> nameIds)
    {
        var checkpoint = new CandidateCheckpoint(_checkpointObserver, _cancellationToken);
        checkpoint.Check();
        var offsets = _block.PostingOffsets;
        var baseTotal = 0L;
        foreach (var nameId in nameIds)
        {
            if (nameId != 0 && nameId <= _baseNameCount)
            {
                var (start, end) = BaseRange(offsets, nameId);
                baseTotal += end - start;
            }
        }

        _rows = ArrayPool<uint>.Shared.Rent((int)Math.Min(Math.Max(baseTotal, 16), Array.MaxLength));
        var addedDelta = false;
        foreach (var nameId in nameIds)
        {
            if (nameId == 0)
            {
                AddUnnamedRows(ref checkpoint);
                continue;
            }

            if (nameId <= _baseNameCount)
            {
                var (start, end) = BaseRange(offsets, nameId);
                AddBaseRows(_block.PostingRows.Slice((int)start, (int)(end - start)), ref checkpoint);
            }

            addedDelta |= AddDeltaRows(nameId, ref checkpoint);
        }

        if (nameIds.Length > 1 || addedDelta)
        {
            SortAndDeduplicate();
        }
    }

    (uint Start, uint End) BaseRange(ReadOnlySpan<uint> offsets, uint nameId)
    {
        var start = offsets[(int)nameId];
        var end = offsets[(int)nameId + 1];
        if (start > end || end > _baseRowCount)
        {
            throw Corrupt($"posting offsets for name {nameId} are [{start}, {end}) against a base of {_baseRowCount} rows");
        }

        return (start, end);
    }

    void AddBaseRows(ReadOnlySpan<uint> list, ref CandidateCheckpoint checkpoint)
    {
        var slotCapacity = _block.SlotCapacity;
        while (!list.IsEmpty)
        {
            checkpoint.Check();
            var chunk = list[..(int)Math.Min(list.Length, CancellationCheckInterval)];
            var invalid = chunk.IndexOfAnyExceptInRange(0u, slotCapacity - 1);
            if (invalid >= 0)
            {
                throw Corrupt($"a posting row is {chunk[invalid]}, past the {slotCapacity} slots");
            }

            EnsureCapacity(_count + chunk.Length);
            chunk.CopyTo(_rows.AsSpan(_count));
            _count += chunk.Length;
            list = list[chunk.Length..];
        }
    }

    bool AddDeltaRows(uint nameId, ref CandidateCheckpoint checkpoint)
    {
        var heads = _block.DeltaHeads;
        var entries = _block.DeltaEntries;
        var index = Volatile.Read(ref heads[(int)(nameId & (_block.BucketCount - 1))]);
        var added = false;
        while (index != 0)
        {
            checkpoint.Step();

            // An index past the published count is unreachable storage or damage: the walk ends as not found, like a
            // damaged name chain.
            if (index > _block.DeltaEntryCount || index >= entries.Length)
            {
                break;
            }

            var entry = entries[(int)index];
            if (index <= _deltaCount && entry.NameId == nameId && entry.Row < RowCount)
            {
                Append(entry.Row);
                added = true;
            }

            if (entry.Next >= index)
            {
                break;
            }

            index = entry.Next;
        }

        return added;
    }

    void AddUnnamedRows(ref CandidateCheckpoint checkpoint)
    {
        var rows = _block.Rows;
        for (var rowIndex = 0u; rowIndex < RowCount; rowIndex++)
        {
            checkpoint.Step();
            var descriptor = FileRow.ReadDescriptorWord(in rows[(int)rowIndex]);
            if (FileRow.DescriptorNameId(descriptor) == 0 && (FileRow.DescriptorFlags(descriptor) & RowFlags.InUse) != 0)
            {
                Append(rowIndex);
            }
        }
    }

    void Append(uint row)
    {
        EnsureCapacity(_count + 1);
        _rows[_count++] = row;
    }

    /// <summary>Grows the rented buffer, at least doubling, so the base lists and the deltas of several names fit.</summary>
    void EnsureCapacity(int needed)
    {
        if (needed <= _rows.Length)
        {
            return;
        }

        var grown = ArrayPool<uint>.Shared.Rent(Math.Max(needed, (int)Math.Min((long)_rows.Length * 2, Array.MaxLength)));
        _rows.AsSpan(0, _count).CopyTo(grown);
        ArrayPool<uint>.Shared.Return(_rows);
        _rows = grown;
    }

    void SortAndDeduplicate()
    {
        var span = _rows.AsSpan(0, _count);
        span.Sort();
        var kept = 0;
        for (var index = 0; index < span.Length; index++)
        {
            if (index == 0 || span[index] != span[index - 1])
            {
                span[kept++] = span[index];
            }
        }

        _count = kept;
    }

    InvalidDataException Corrupt(string failure)
    {
        var message = $"Block file '{_block.Path}' has a corrupt posting region: {failure}.";
        DiagnosticsReport.Send(_block.Diagnostics, message);
        return new InvalidDataException(message);
    }

    public void Dispose()
    {
        var rows = Interlocked.Exchange(ref _rows, []);
        if (rows.Length > 0)
        {
            ArrayPool<uint>.Shared.Return(rows);
        }

        _count = 0;
    }
}

/// <summary>
///     The cancellation cadence of a posting pass: the first step is a checkpoint, then one every
///     <see cref="PostingCandidates.CancellationCheckInterval" /> steps, rejected rows and stale postings included.
/// </summary>
struct CandidateCheckpoint
{
    readonly CancellationToken _cancellationToken;
    readonly Action? _observer;
    uint _stepsUntilCheck;

    internal CandidateCheckpoint(Action? observer, CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _observer = observer;
        _stepsUntilCheck = 1;
    }

    /// <summary>Counts one visited entry or verified row, checking the token when the interval is due.</summary>
    internal void Step()
    {
        _stepsUntilCheck--;
        if (_stepsUntilCheck == 0)
        {
            Check();
            _stepsUntilCheck = PostingCandidates.CancellationCheckInterval;
        }
    }

    /// <summary>Checks the token now.</summary>
    internal void Check()
    {
        _observer?.Invoke();
        _cancellationToken.ThrowIfCancellationRequested();
    }
}
