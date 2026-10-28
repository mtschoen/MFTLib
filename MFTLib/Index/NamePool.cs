using System.Runtime.InteropServices;
using System.Text;

namespace MFTLib.Index;

/// <summary>
///     Append-only interned names. A name is stored once per exact spelling: case variants are distinct
///     entries that share one hash chain and tag, because the hash folds case. Callers serialize additions through
///     the block writer gate; readers keep the block borrowed. Bytes and entries become immutable before the bucket
///     head that publishes them. Chains are never trusted: every step is bounds-checked and must move to a smaller
///     identifier, so a corrupt chain can miss a match but never read outside the block or loop.
/// </summary>
static class NamePool
{
    /// <summary>The entry's length field is 15 bits, so a name longer than this cannot be stored.</summary>
    internal const int MaximumNameLengthUnits = 32767;

    /// <summary>Reads one name by identifier; identifier zero is the empty name.</summary>
    /// <exception cref="InvalidDataException">The identifier or its entry lies outside the published regions.</exception>
    internal static NameView Read(BlockFile block, uint nameId)
    {
        ArgumentNullException.ThrowIfNull(block);
        // Touch the region even for zero, preserving the disposed check on an empty name.
        var entries = block.NameEntries;
        if (nameId == 0)
        {
            return default;
        }

        if (nameId > block.NameEntryCount || nameId >= (uint)entries.Length)
        {
            throw Corrupt(block, nameId, "the identifier is outside the published entries");
        }

        // Shared section storage is hostile: capture the entry once and validate it before slicing.
        var entry = entries[(int)nameId];
        var failure = CheckEntry(block, entry);
        if (failure is not null)
        {
            throw Corrupt(block, nameId, failure);
        }

        return ReadEntry(block, entry);
    }

    /// <summary>Reads one row's name from a single descriptor-word read.</summary>
    internal static NameView ReadRowName(BlockFile block, uint rowIndex)
    {
        ArgumentNullException.ThrowIfNull(block);
        var descriptor = FileRow.ReadDescriptorWord(in block.Rows[(int)rowIndex]);
        return Read(block, FileRow.DescriptorNameId(descriptor));
    }

    /// <summary>Finds the entry whose spelling equals <paramref name="name" /> exactly; the empty name is identifier zero.</summary>
    internal static bool TryFindOrdinal(BlockFile block, ReadOnlySpan<char> name, out uint nameId) =>
        TryFindOrdinal(block, name, null, CancellationToken.None, out nameId);

    /// <summary>
    ///     As above, observing cancellation every <see cref="ChainWalker.CheckpointIntervalEntries" /> visited entries,
    ///     tag-rejected ones included, and calling <paramref name="checkpointObserver" /> at each check.
    /// </summary>
    internal static bool TryFindOrdinal(BlockFile block, ReadOnlySpan<char> name, Action? checkpointObserver,
        CancellationToken cancellationToken, out uint nameId)
    {
        ArgumentNullException.ThrowIfNull(block);
        nameId = 0;
        if (name.IsEmpty)
        {
            return true;
        }

        var walker = WalkChain(block, NameHash.Compute(name), checkpointObserver, cancellationToken);
        while (walker.MoveNext(out var candidate, out var entry))
        {
            if (TryReadEntry(block, entry, out var view) && view.EqualsOrdinal(name))
            {
                nameId = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Chain links walked in one bucket before the writer indexes that whole bucket in memory.</summary>
    const int LongChainThreshold = 32;

    /// <summary>
    ///     The writer's lookup of an exact spelling. A short bucket is walked. Once a walk visits more than
    ///     <see cref="LongChainThreshold" /> physical links, whatever their tags, the writer builds one ordinal index of
    ///     the whole bucket (exact spelling to identifier, across every tag) from a single raw walk and keeps it current
    ///     on every add, so interning is amortized linear however many casings or colliding names share the bucket. The
    ///     index lives with the writer's <see cref="BlockFile" /> instance, never in the block file, and opening a block
    ///     builds none.
    /// </summary>
    static bool TryFindForWrite(BlockFile block, ReadOnlySpan<char> name, ulong hash, out uint nameId)
    {
        nameId = 0;
        if (name.IsEmpty)
        {
            return true;
        }

        var bucket = BucketOf(block, hash);
        if (block.TryGetOrdinalLookup(bucket, out var lookup))
        {
            return lookup.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out nameId);
        }

        var tag = (ushort)(hash >> 48);
        var visited = 0;
        var walker = new ChainWalker(block, hash, null, CancellationToken.None, anyTag: true);
        while (walker.MoveNext(out var candidate, out var entry))
        {
            if (++visited > LongChainThreshold)
            {
                lookup = BuildOrdinalLookup(block, hash);
                block.SetOrdinalLookup(bucket, lookup);
                return lookup.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out nameId);
            }

            if (entry.HashTag == tag && TryReadEntry(block, entry, out var view) && view.EqualsOrdinal(name))
            {
                nameId = candidate;
                return true;
            }
        }

        return false;
    }

    static int BucketOf(BlockFile block, ulong hash) => (int)(hash & (block.BucketCount - 1));

    static Dictionary<string, uint> BuildOrdinalLookup(BlockFile block, ulong hash)
    {
        var lookup = new Dictionary<string, uint>(StringComparer.Ordinal);
        var walker = new ChainWalker(block, hash, null, CancellationToken.None, anyTag: true);
        while (walker.MoveNext(out var candidate, out var entry))
        {
            if (TryReadEntry(block, entry, out var view))
            {
                // Newest first; an exact spelling is unique, so the first insert is the only one.
                lookup.TryAdd(view.ToString(), candidate);
            }
        }

        return lookup;
    }

    /// <summary>
    ///     Returns the identifier of the exact spelling, adding it when absent. A hit succeeds even when entries or
    ///     the pool are full; a miss fails when either is full or the name is too long.
    /// </summary>
    internal static bool TryGetOrAdd(BlockFile block, ReadOnlySpan<char> name, out uint nameId)
    {
        ArgumentNullException.ThrowIfNull(block);
        var hash = NameHash.Compute(name);
        if (TryFindForWrite(block, name, hash, out nameId) || name.IsEmpty)
        {
            return true;
        }

        if (name.Length > MaximumNameLengthUnits || block.NameEntryCount >= block.NameEntryCapacity)
        {
            return false;
        }

        var isUtf16 = !Ascii.IsValid(name);
        var used = block.NamePoolUsed;
        var offset = isUtf16 ? ((ulong)used + 1) & ~1UL : used;
        var byteCount = name.Length * (isUtf16 ? sizeof(char) : 1);
        if (offset + (uint)byteCount > block.NamePoolCapacity)
        {
            return false;
        }

        var destination = block.GetNamePoolBytes((uint)offset, byteCount);
        if (isUtf16)
        {
            name.CopyTo(MemoryMarshal.Cast<byte, char>(destination));
        }
        else
        {
            Ascii.FromUtf16(name, destination, out _);
        }

        var buckets = block.Buckets;
        var bucket = (int)(hash & (block.BucketCount - 1));
        var previous = Volatile.Read(ref buckets[bucket]);
        if (previous > block.NameEntryCount)
        {
            return false;
        }

        nameId = block.NameEntryCount + 1;
        block.NameEntries[(int)nameId] = new NameEntry
        {
            PoolOffsetBytes = (uint)offset,
            LengthAndEncoding = (ushort)(name.Length | (isUtf16 ? 0x8000 : 0)),
            HashTag = (ushort)(hash >> 48),
            Next = previous
        };
        // Publication order, each step a release for the readers' acquires below:
        //   1. pool bytes and the whole entry were written above with plain stores;
        //   2. PublishNamePoolUsed: Volatile.Write of the used length;
        //   3. PublishNameEntryCount: Volatile.Write of the count;
        //   4. Volatile.Write of the bucket head, last, which is what makes the entry reachable.
        // A reader that Volatile.Reads the head and then Volatile.Reads the count therefore sees a count that covers
        // the head, and sees the used length and entry bytes the head's entry needs.
        block.PublishNamePoolUsed((uint)offset + (uint)byteCount);
        block.PublishNameEntryCount(nameId);
        Volatile.Write(ref buckets[bucket], nameId);
        block.AddToOrdinalLookup(BucketOf(block, hash), name, nameId);
        return true;
    }

    /// <summary>
    ///     Starts a walk over the entries whose hash chain and tag match <paramref name="hash" />, newest first. The
    ///     published entry count is captured now, so entries added later are not visited.
    /// </summary>
    internal static ChainWalker WalkChain(BlockFile block, ulong hash) => new(block, hash, null, CancellationToken.None);

    /// <summary>A walk that observes cancellation while it traverses; see <see cref="ChainWalker" />.</summary>
    internal static ChainWalker WalkChain(BlockFile block, ulong hash, Action? checkpointObserver,
        CancellationToken cancellationToken) => new(block, hash, checkpointObserver, cancellationToken);

    /// <summary>Rebuilds every chain from the immutable entries, as a replacement block with a new bucket count needs.</summary>
    internal static void RebuildBuckets(BlockFile block)
    {
        var buckets = block.Buckets;
        buckets.Clear();
        block.ClearOrdinalLookups();
        var entries = block.NameEntries;
        for (uint nameId = 1; nameId <= block.NameEntryCount; nameId++)
        {
            var hash = NameHash.Compute(Read(block, nameId));
            var bucket = (int)(hash & (block.BucketCount - 1));
            entries[(int)nameId].Next = buckets[bucket];
            Volatile.Write(ref buckets[bucket], nameId);
        }
    }

    /// <summary>The first read-safety failure of an entry, or null when it can be sliced safely.</summary>
    internal static string? CheckEntry(BlockFile block, NameEntry entry)
    {
        if (entry.Length == 0)
        {
            return "the entry has zero length";
        }

        if (entry.IsUtf16 && (entry.PoolOffsetBytes & 1) != 0)
        {
            return "the UTF-16 entry starts at an odd pool offset";
        }

        var byteCount = (uint)entry.Length * (entry.IsUtf16 ? sizeof(char) : 1u);
        return (ulong)entry.PoolOffsetBytes + byteCount > block.NamePoolUsed
            ? "the entry extends past the used name pool"
            : null;
    }

    /// <summary>Reads an entry without throwing; false when the entry fails the read-safety checks.</summary>
    internal static bool TryReadEntry(BlockFile block, NameEntry entry, out NameView view)
    {
        if (CheckEntry(block, entry) is not null)
        {
            view = default;
            return false;
        }

        view = ReadEntry(block, entry);
        return true;
    }

    static NameView ReadEntry(BlockFile block, NameEntry entry)
    {
        return new NameView(block.GetNamePoolBytes(entry.PoolOffsetBytes,
            entry.Length * (entry.IsUtf16 ? sizeof(char) : 1)), entry.IsUtf16);
    }

    static InvalidDataException Corrupt(BlockFile block, uint nameId, string failure)
    {
        var message = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Block file '{block.Path}' has a corrupt name entry {nameId}: {failure}.");
        DiagnosticsReport.Send(block.Diagnostics, message);
        return new InvalidDataException(message);
    }

    /// <summary>Walks one hash chain newest first, treating every link as untrusted input.</summary>
    internal ref struct ChainWalker
    {
        /// <summary>Cancellation is observed once per this many visited entries, tag-rejected ones included.</summary>
        internal const uint CheckpointIntervalEntries = 4096;

        readonly BlockFile _block;
        readonly bool _anyTag;
        readonly Span<NameEntry> _entries;
        readonly uint _count;
        readonly ushort _tag;
        readonly Action? _checkpointObserver;
        readonly CancellationToken _cancellationToken;
        uint _current;
        uint _visited;

        internal ChainWalker(BlockFile block, ulong hash, Action? checkpointObserver, CancellationToken cancellationToken,
            bool anyTag = false)
        {
            _block = block;
            _anyTag = anyTag;
            _checkpointObserver = checkpointObserver;
            _cancellationToken = cancellationToken;
            _entries = block.NameEntries;
            _tag = (ushort)(hash >> 48);
            // Acquire order: the head first, the published count second. A writer publishes the count before the head,
            // so a head read here is always covered by the count read after it, even if the writer appends in between.
            // Reading the count first would let a newer head exceed it and hide the whole valid chain.
            _current = Volatile.Read(ref block.Buckets[(int)(hash & (block.BucketCount - 1))]);
            Checkpoint();
            _count = block.NameEntryCount;
        }

        void Checkpoint()
        {
            _checkpointObserver?.Invoke();
            _cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        ///     Advances to the next entry with the walk's tag (any tag for a raw walk). A link outside the published entries, or one that does not
        ///     move to a smaller identifier, ends the walk.
        /// </summary>
        internal bool MoveNext(out uint nameId, out NameEntry entry)
        {
            while (_current != 0)
            {
                if (_current > _count || _current >= (uint)_entries.Length)
                {
                    _current = 0;
                    break;
                }

                _block.CountChainLinkVisit();
                if (++_visited % CheckpointIntervalEntries == 0)
                {
                    Checkpoint();
                }

                nameId = _current;
                entry = _entries[(int)_current];
                _current = entry.Next < nameId ? entry.Next : 0;
                if (_anyTag || entry.HashTag == _tag)
                {
                    return true;
                }
            }

            nameId = 0;
            entry = default;
            return false;
        }
    }
}
