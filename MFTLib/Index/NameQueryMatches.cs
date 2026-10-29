using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

/// <summary>
///     The first pass of a name query: resolves which interned entries match once, so the second pass over rows is a
///     membership test on the row's name identifier. An entry pass captures the published entry count, so a name a
///     live watch interns afterwards is outside this pass, while row contents stay live.
/// </summary>
sealed class NameQueryMatches : IDisposable
{
    /// <summary>Cancellation is observed before setup and once per this many name entries.</summary>
    internal const uint CancellationCheckIntervalEntries = 4096;

    readonly bool _allNames;
    readonly uint _entryCount;
    readonly bool _usesBitset;
    readonly Action? _checkpointObserver;
    ulong[]? _matchingNames;
    NameIdCandidates _candidates;

    internal NameQueryMatches(BlockFile block, SearchQuery query, CancellationToken cancellationToken)
        : this(block, query, null, cancellationToken)
    {
    }

    /// <summary>Builds the pass, calling <paramref name="checkpointObserver" /> at each cancellation check.</summary>
    internal NameQueryMatches(BlockFile block, SearchQuery query, Action? checkpointObserver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(query);
        _checkpointObserver = checkpointObserver;
        Checkpoint(cancellationToken);
        _entryCount = block.NameEntryCount;
        // Observed again once the count is fixed, before any chain is set up: the window in which a live writer can
        // publish a name this pass must ignore.
        Checkpoint(cancellationToken);
        if (query.NamePattern is null)
        {
            _allNames = true;
            return;
        }

        var pattern = new NamePattern(query.NamePattern, query.MatchMode, query.CaseSensitive);
        try
        {
            if (query.MatchMode == NameMatchMode.Exact)
            {
                AddExactCandidates(block, pattern, query.CaseSensitive, cancellationToken);
                _candidates.Seal();
                return;
            }

            _usesBitset = true;
            _matchingNames = BuildBitset(block, pattern, cancellationToken);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Whether the pass resolved a set of exact-match identifiers, which postings can enumerate, rather than a bitset or every name.</summary>
    internal bool IsExactCandidates => !_allNames && !_usesBitset;

    /// <summary>The published entry count this pass captured; identifiers above it are outside the pass.</summary>
    internal uint EntryCount => _entryCount;

    /// <summary>How many identifiers the exact pass matched.</summary>
    internal int CandidateCount => _candidates.Count;

    /// <summary>The <paramref name="index" />th matched identifier of an exact pass.</summary>
    internal uint CandidateAt(int index) => _candidates[index];

    /// <summary>Whether some candidate spells the pattern exactly, ignoring the case setting.</summary>
    internal bool HasOrdinalMatch { get; private set; }

    /// <summary>The identifier of the entry that spells the pattern exactly; meaningful only with <see cref="HasOrdinalMatch" />.</summary>
    internal uint OrdinalMatchId { get; private set; }

    [SuppressMessage("Roslynator", "RCS1242",
        Justification = "FileRow is explicit-layout and intentionally mutable for field-by-field disk mapping.")]
    internal bool Matches(in FileRow row)
    {
        if (_allNames)
        {
            return true;
        }

        var nameId = FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in row));
        if (_usesBitset)
        {
            // A watch can append names after this query's entry pass. They are outside this pass.
            return nameId <= _entryCount && _matchingNames is { } bits &&
                   (bits[(int)(nameId >> 6)] & (1UL << (int)(nameId & 63))) != 0;
        }

        // Like the bitset branch: a name interned after the entry pass is outside it.
        return nameId <= _entryCount && _candidates.Contains(nameId);
    }

    /// <summary>Whether the row carries the entry that spells the pattern exactly.</summary>
    [SuppressMessage("Roslynator", "RCS1242",
        Justification = "FileRow is explicit-layout and intentionally mutable for field-by-field disk mapping.")]
    internal bool IsOrdinalMatch(in FileRow row) =>
        HasOrdinalMatch && FileRow.DescriptorNameId(FileRow.ReadDescriptorWord(in row)) == OrdinalMatchId;

    void AddExactCandidates(BlockFile block, NamePattern pattern, bool caseSensitive,
        CancellationToken cancellationToken)
    {
        if (caseSensitive || pattern.Text.Length == 0)
        {
            if (NamePool.TryFindOrdinal(block, pattern.Text, _checkpointObserver, cancellationToken, out var nameId) &&
                nameId <= _entryCount)
            {
                _candidates.Add(nameId);
                HasOrdinalMatch = true;
                OrdinalMatchId = nameId;
            }

            return;
        }

        // Every casing of a name shares the folded hash, so one chain holds all of its spellings.
        var scratch = Array.Empty<char>();
        var walker = NamePool.WalkChain(block, NameHash.Compute(pattern.Text), _checkpointObserver, cancellationToken);
        while (walker.MoveNext(out var nameId, out var entry))
        {
            if (nameId > _entryCount || !NamePool.TryReadEntry(block, entry, out var view) || !pattern.Matches(view, ref scratch))
            {
                continue;
            }

            _candidates.Add(nameId);
            if (!HasOrdinalMatch && view.EqualsOrdinal(pattern.Text))
            {
                HasOrdinalMatch = true;
                OrdinalMatchId = nameId;
            }
        }
    }

    ulong[] BuildBitset(BlockFile block, NamePattern pattern, CancellationToken cancellationToken)
    {
        var wordCount = checked((int)(((ulong)_entryCount + 64) / 64));
        var bits = ArrayPool<ulong>.Shared.Rent(wordCount);
        bits.AsSpan(0, wordCount).Clear();
        try
        {
            var scratch = Array.Empty<char>();
            for (var nameId = 0u; nameId <= _entryCount; nameId++)
            {
                if (nameId % CancellationCheckIntervalEntries == 0 && nameId != 0)
                {
                    Checkpoint(cancellationToken);
                }

                var view = nameId == 0 ? default : NamePool.Read(block, nameId);
                if (pattern.Matches(view, ref scratch))
                {
                    bits[(int)(nameId >> 6)] |= 1UL << (int)(nameId & 63);
                }
            }

            Checkpoint(cancellationToken);
            return bits;
        }
        catch
        {
            ArrayPool<ulong>.Shared.Return(bits);
            throw;
        }
    }

    void Checkpoint(CancellationToken cancellationToken)
    {
        _checkpointObserver?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        var matchingNames = Interlocked.Exchange(ref _matchingNames, null);
        if (matchingNames is not null)
        {
            ArrayPool<ulong>.Shared.Return(matchingNames);
        }

        _candidates.Dispose();
    }
}
