using System.Diagnostics.CodeAnalysis;

namespace MFTLib.Index;

sealed unsafe partial class BlockFile
{
    long _nameRegionAccessCount;

    /// <summary>
    ///     Receives a line when a name read finds corruption, then the read throws. Set by the root runtime to
    ///     <see cref="FileIndexOptions.Diagnostics" /> wherever it creates or adopts a block.
    /// </summary>
    internal Action<string>? Diagnostics { get; set; }

    /// <summary>
    ///     Counts accesses to the name entry, bucket and byte-pool regions. Incremented without atomics: exact under one
    ///     thread and approximate under concurrency. Tests assert that opening a block touches none of them.
    /// </summary>
    internal long NameRegionAccessCount => Volatile.Read(ref _nameRegionAccessCount);

    /// <summary>The immutable name entries, <see cref="NameEntryCapacity" /> plus one long, including reserved entry zero.</summary>
    internal Span<NameEntry> NameEntries
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CountNameRegionAccess();
            return new Span<NameEntry>(_base + _nameEntryRegionOffset, (int)NameEntryCapacity + 1);
        }
    }

    /// <summary>The hash bucket heads, published with volatile stores.</summary>
    internal Span<uint> Buckets
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CountNameRegionAccess();
            return new Span<uint>(_base + _bucketRegionOffset, (int)BucketCount);
        }
    }

    /// <summary>The whole byte pool, ASCII and UTF-16LE names packed back to back.</summary>
    internal Span<byte> NamePoolBytes => GetNamePoolBytes(0, checked((int)NamePoolCapacity));

    /// <summary>Returns a bounded slice of the byte pool without narrowing the whole pool capacity.</summary>
    internal Span<byte> GetNamePoolBytes(uint offset, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if ((ulong)offset + (uint)length > NamePoolCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        CountNameRegionAccess();
        return new Span<byte>(_base + _namePoolOffset + offset, length);
    }

    Dictionary<int, Dictionary<string, uint>>? _ordinalLookups;

    /// <summary>The writer's in-memory ordinal index of one long bucket, if it built one. Writer thread only.</summary>
    internal bool TryGetOrdinalLookup(int key, [NotNullWhen(true)] out Dictionary<string, uint>? lookup)
    {
        lookup = null;
        return _ordinalLookups?.TryGetValue(key, out lookup) ?? false;
    }

    internal void SetOrdinalLookup(int key, Dictionary<string, uint> lookup) =>
        (_ordinalLookups ??= [])[key] = lookup;

    /// <summary>Keeps an existing long-chain index current after the writer adds a spelling to that chain.</summary>
    internal void AddToOrdinalLookup(int key, ReadOnlySpan<char> name, uint nameId)
    {
        if (_ordinalLookups is not null && _ordinalLookups.TryGetValue(key, out var lookup))
        {
            lookup[name.ToString()] = nameId;
        }
    }

    internal void ClearOrdinalLookups() => _ordinalLookups = null;

    long _chainLinkVisitCount;

    /// <summary>
    ///     Counts every hash-chain link a walk visits, tag-rejected links included. Plain increments, exact under one
    ///     thread. Tests assert that interning does work proportional to the names added, not to the chain length.
    /// </summary>
    internal long ChainLinkVisitCount => Volatile.Read(ref _chainLinkVisitCount);

    internal void CountChainLinkVisit() => _chainLinkVisitCount++;

    void CountNameRegionAccess() => _nameRegionAccessCount++;
}
