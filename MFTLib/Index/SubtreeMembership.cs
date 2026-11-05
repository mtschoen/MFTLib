using System.Buffers;

namespace MFTLib.Index;

/// <summary>
///     Answers "is this row inside the subtree rooted at the anchor row?" for every row of one query over one root
///     block, climbing each directory's ancestry once instead of once per file. Two bit sets cache the verdicts: a
///     row's bit in <c>known</c> says its verdict was decided, its bit in <c>inside</c> says what it was.
/// </summary>
/// <remarks>
///     <para>
///         The cache is shared by a query's parallel partitions. A writer publishes a row's <c>inside</c> bit with
///         <see cref="Interlocked.Or(ref ulong, ulong)" /> before it publishes the row's <c>known</c> bit, and a reader
///         reads <c>known</c> with <see cref="Volatile.Read(ref readonly ulong)" /> before it reads <c>inside</c>, so a reader that sees
///         <c>known</c> also sees the final <c>inside</c>. Verdicts are deterministic for a fixed block, so two
///         writers racing on one row publish the same bits.
///     </para>
///     <para>
///         The row count is captured at construction: a live watch can append rows while the query runs, and a
///         row at or past the captured count is answered by climbing without caching.
///     </para>
/// </remarks>
sealed class SubtreeMembership : IDisposable
{
    // A cached distance this large means "at least this far": a byte per row keeps the cache small, and only chains
    // deeper than this, which are rare, climb uncached past it.
    const byte SaturatedDistance = byte.MaxValue;

    readonly BlockFile _block;
    readonly uint _anchorRow;
    readonly uint _rowCount;
    readonly SubtreeMembership? _outer;
    ulong[]? _known;
    ulong[]? _inside;
    byte[]? _depth;
    long _parentReads;

    /// <summary>Tests membership in the subtree of one row of <paramref name="block" />.</summary>
    /// <param name="block">The block whose parent column is climbed.</param>
    /// <param name="anchorRow">The row whose subtree is tested.</param>
    /// <param name="outer">A restriction every member must also satisfy (a directory view); owned and disposed with this one.</param>
    internal SubtreeMembership(BlockFile block, uint anchorRow, SubtreeMembership? outer = null)
    {
        _block = block;
        _anchorRow = anchorRow;
        _outer = outer;
        _rowCount = block.Header.RowCount;
        var wordCount = (int)(((long)_rowCount + 63) / 64);
        _known = Rent(wordCount);
        _inside = Rent(wordCount);
        _depth = ArrayPool<byte>.Shared.Rent(Math.Max((int)_rowCount, 1));
    }

    /// <summary>
    ///     Parent-column reads made while climbing. Exact only when one thread calls <see cref="Contains" />; the
    ///     parallel partitions race on it, and no decision reads it.
    /// </summary>
    internal long ParentReads => _parentReads;

    static ulong[] Rent(int wordCount)
    {
        var words = ArrayPool<ulong>.Shared.Rent(Math.Max(wordCount, 1));
        Array.Clear(words, 0, wordCount);
        return words;
    }

    /// <summary>
    ///     Whether <paramref name="row" /> is the anchor or lies below it in the block's parent chain, and below the
    ///     outer restriction when there is one.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     The parent chain from <paramref name="row" /> does not end within
    ///     <see cref="BlockLayout.MaximumPathDepth" /> hops and is not a cycle. The verdict cache stores each row's
    ///     distance to its chain's end (saturating, so a deeper row climbs uncached), so this does not depend on the
    ///     order rows are asked in.
    /// </exception>
    internal bool Contains(uint row) => (_outer is null || _outer.Contains(row)) && ContainsBelowAnchor(row);

    bool ContainsBelowAnchor(uint row)
    {
        // The rows this climb visited, to record one verdict for all of them once it is known.
        var visited = new ParentChainBuffer(stackalloc uint[ParentChainBuffer.StackCapacity]);
        if (!TryClimbToVerdict(row, ref visited, out var verdict, out var endDistance) ||
            endDistance > BlockLayout.MaximumPathDepth)
        {
            // A climb that ran out of hops, met a cycle, reached a row whose cached distance saturated, or whose
            // cached tail makes the chain longer than the cap is answered by one uncached climb, so the answer (a
            // cycle is outside, a chain deeper than the cap throws) never depends on which rows were asked first,
            // and nothing is recorded from it.
            return ClimbWithoutCache(row);
        }

        Record(visited.Values, verdict, endDistance);
        return verdict;
    }

    // Climbs until the anchor, a top or a row with a cached verdict decides; false when the climb cannot decide.
    // endDistance is the hops from the start to where the climb stopped, plus the stopping row's cached distance.
    bool TryClimbToVerdict(uint row, ref ParentChainBuffer visited, out bool verdict, out int endDistance)
    {
        var known = _known ?? throw new ObjectDisposedException(nameof(SubtreeMembership));
        var inside = _inside ?? throw new ObjectDisposedException(nameof(SubtreeMembership));
        var depth = _depth ?? throw new ObjectDisposedException(nameof(SubtreeMembership));
        var current = row;
        verdict = false;
        endDistance = 0;
        for (var hops = 0; hops <= BlockLayout.MaximumPathDepth; hops++)
        {
            if (current == _anchorRow)
            {
                visited.Add(current);
                verdict = true;
                endDistance = hops;
                return true;
            }

            if (current < _rowCount)
            {
                var bit = 1UL << (int)(current & 63);
                var word = (int)(current >> 6);
                if ((Volatile.Read(ref known[word]) & bit) != 0)
                {
                    verdict = (Volatile.Read(ref inside[word]) & bit) != 0;
                    endDistance = hops + depth[current];
                    return depth[current] != SaturatedDistance;
                }
            }

            // Past the stack storage a repeat is a cycle; a shorter cycle is caught once the climb reaches that length.
            if (hops == BlockLayout.MaximumPathDepth ||
                (visited.Count >= ParentChainBuffer.StackCapacity && visited.Contains(current)))
            {
                return false;
            }

            visited.Add(current);
            _parentReads++;
            if (!IndexNavigation.TryGetParentRow(_block, current, out var parent))
            {
                endDistance = hops;
                return true;
            }

            current = parent;
        }

        return false;
    }

    // The uncached climb: the anchor within the cap is inside, a top is outside, a row repeating within the cap is a
    // cycle and outside, and a chain still made of new rows after the cap throws.
    bool ClimbWithoutCache(uint row)
    {
        var seen = new ParentChainBuffer(stackalloc uint[ParentChainBuffer.StackCapacity]);
        var current = row;
        for (var hops = 0; hops <= BlockLayout.MaximumPathDepth; hops++)
        {
            if (current == _anchorRow)
            {
                return true;
            }

            if (seen.Contains(current))
            {
                return false;
            }

            seen.Add(current);
            if (hops == BlockLayout.MaximumPathDepth)
            {
                break;
            }

            if (!IndexNavigation.TryGetParentRow(_block, current, out current))
            {
                return false;
            }
        }

        if (!IndexNavigation.TryGetParentRow(_block, current, out _))
        {
            return false;
        }

        throw new InvalidDataException(
            $"Parent chain for row {row} exceeds the supported depth of {BlockLayout.MaximumPathDepth} hops.");
    }

    // Each visited row's distance to the chain's end is the total minus its own position, saturating at
    // SaturatedDistance, which reads back as "at least this far" and sends a later climb through it uncached.
    void Record(ReadOnlySpan<uint> rows, bool verdict, int endDistance)
    {
        var known = _known ?? throw new ObjectDisposedException(nameof(SubtreeMembership));
        var inside = _inside ?? throw new ObjectDisposedException(nameof(SubtreeMembership));
        var depth = _depth ?? throw new ObjectDisposedException(nameof(SubtreeMembership));
        for (var position = 0; position < rows.Length; position++)
        {
            var visitedRow = rows[position];
            if (visitedRow >= _rowCount)
            {
                continue;
            }

            depth[visitedRow] = (byte)Math.Clamp(endDistance - position, 0, SaturatedDistance);
            var bit = 1UL << (int)(visitedRow & 63);
            var word = (int)(visitedRow >> 6);
            if (verdict)
            {
                Interlocked.Or(ref inside[word], bit);
            }

            // The depth byte is written before `known` is published, and read only after `known` is seen.
            Interlocked.Or(ref known[word], bit);
        }
    }

    public void Dispose()
    {
        var known = Interlocked.Exchange(ref _known, null);
        var inside = Interlocked.Exchange(ref _inside, null);
        var depth = Interlocked.Exchange(ref _depth, null);
        _outer?.Dispose();
        if (known is not null)
        {
            ArrayPool<ulong>.Shared.Return(known);
        }

        if (inside is not null)
        {
            ArrayPool<ulong>.Shared.Return(inside);
        }

        if (depth is not null)
        {
            ArrayPool<byte>.Shared.Return(depth);
        }
    }
}
