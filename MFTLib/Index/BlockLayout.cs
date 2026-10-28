namespace MFTLib.Index;

/// <summary>
///     Byte geometry of a packed index block: one 4 KB header page, a dense region of
///     32-byte rows indexed by record number, a per-row NTFS sequence-number region,
///     12-byte interned name entries, a hash bucket directory and an append-only ASCII and
///     UTF-16LE byte pool. Every region boundary is 4 KB aligned so a mapped view can be
///     paged independently. Entry-dependent regions are sized by the entry capacity E, which
///     the header stores separately from the slot capacity S.
/// </summary>
static class BlockLayout
{
    /// <summary>The ASCII bytes 'M', 'L', 'I', 'X' read as a little-endian unsigned 32-bit value.</summary>
    public const uint Magic = 0x58494C4D;

    /// <summary>A mismatch means discard the block and rescan. There is no migration path.</summary>
    public const uint FormatVersion = 8;

    /// <summary>Alignment, in bytes, of each on-disk block region.</summary>
    public const int PageSize = 4096;

    /// <summary>Bytes actually occupied by header fields. The header region is padded to a page.</summary>
    public const int HeaderFieldBytes = 160;

    /// <summary>Total header-region size in bytes, including padding.</summary>
    public const int HeaderRegionBytes = PageSize;

    /// <summary>Size, in bytes, of one fixed-size row.</summary>
    public const int RowBytes = 32;

    /// <summary>Headroom is 25 percent of the estimate or this many rows, whichever is larger.</summary>
    const int MinimumSlotHeadroomRows = 65536;

    /// <summary>Floor of the pool reservation, so a small volume still has room for renames.</summary>
    const long MinimumNamePoolBytes = 1048576;

    /// <summary>Reserved per slot; measured use is 2.7 to 15.2 bytes per slot, so this is generous headroom.</summary>
    const long NamePoolBytesPerSlot = 24;

    /// <summary>Page-aligned ceiling under 2 GiB, so a pool span's length always fits an int.</summary>
    internal const uint MaximumNamePoolCapacity = 0x7FFFF000;

    /// <summary>Floor of the bucket directory, so a tiny block still spreads its chains.</summary>
    const uint MinimumBucketCount = 1024;

    /// <summary>Target chain length: one head per this many entries before rounding up to a power of two.</summary>
    const uint EntriesPerBucket = 4;

    /// <summary>Mirrors the native resolver's cap so a corrupt parent column cannot loop forever.</summary>
    public const int MaximumPathDepth = 128;

    /// <summary>
    ///     The parent row of a row with no verified parent: a freed or deleted record whose parent chain could not be
    ///     trusted or whose ancestor slot was reused. No row has this index, so the row is nobody's child and its
    ///     path is its bare name.
    /// </summary>
    internal const uint DetachedParentRow = uint.MaxValue;

    /// <summary>Size, in bytes, of one stored NTFS sequence number.</summary>
    const int SequenceBytes = 2;

    /// <summary>
    ///     Fixed, not computed: the header region is exactly one page, so this can never be
    ///     anything other than <see cref="HeaderRegionBytes" />.
    /// </summary>
    public const long RowRegionOffset = HeaderRegionBytes;

    /// <summary>Rounds a non-negative byte count up to the specified positive alignment.</summary>
    /// <param name="value">Non-negative value to align.</param>
    /// <param name="alignment">Positive alignment in bytes.</param>
    /// <returns>The smallest multiple of <paramref name="alignment" /> no less than <paramref name="value" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value" /> is negative or <paramref name="alignment" /> is not positive.</exception>
    internal static long AlignUp(long value, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    /// <summary>Computes row capacity by adding the required headroom to an estimated row count.</summary>
    /// <param name="estimatedRowCount">Estimated number of rows required by the initial scan.</param>
    /// <returns>The estimate plus headroom of 25 percent of the estimate or <see cref="MinimumSlotHeadroomRows" /> rows, whichever is larger.</returns>
    /// <exception cref="OverflowException">The estimate plus required headroom exceeds <see cref="UInt32.MaxValue" />.</exception>
    public static uint ComputeSlotCapacity(uint estimatedRowCount)
    {
        var headroom = Math.Max(estimatedRowCount / 4, MinimumSlotHeadroomRows);
        return checked(estimatedRowCount + headroom);
    }

    /// <summary>Doubles a capacity while leaving room for reserved name entry zero in an int-indexed span.</summary>
    /// <param name="capacity">The capacity to double.</param>
    /// <returns>Twice <paramref name="capacity" />, or the clamp when that is larger.</returns>
    internal static uint DoubleCapacity(uint capacity)
    {
        return (uint)Math.Min(capacity * 2UL, int.MaxValue - 1UL);
    }

    /// <summary>Doubles a name pool capacity, clamped to <see cref="MaximumNamePoolCapacity" />.</summary>
    internal static uint DoubleNamePoolCapacity(uint capacity)
    {
        return (uint)Math.Min(capacity * 2UL, MaximumNamePoolCapacity);
    }

    /// <summary>Reserves name pool bytes for a slot capacity: 24 bytes per slot, at least 1 MiB, page aligned.</summary>
    /// <param name="slotCapacity">Row slot capacity, headroom included.</param>
    /// <returns>The pool capacity in bytes, clamped to <see cref="MaximumNamePoolCapacity" />.</returns>
    internal static uint ComputeNamePoolCapacity(uint slotCapacity)
    {
        var reserved = Math.Max(MinimumNamePoolBytes, slotCapacity * NamePoolBytesPerSlot);
        return (uint)Math.Min(AlignUp(reserved, PageSize), MaximumNamePoolCapacity);
    }

    /// <summary>Gets the page-aligned byte length of the row region.</summary>
    /// <param name="slotCapacity">Number of rows allocated in the block.</param>
    /// <returns>Page-aligned row-region length in bytes.</returns>
    internal static long RowRegionBytes(uint slotCapacity)
    {
        return AlignUp((long)slotCapacity * RowBytes, PageSize);
    }

    /// <summary>Gets the byte offset at which per-row sequence numbers begin.</summary>
    /// <param name="slotCapacity">Number of rows allocated in the block.</param>
    /// <returns>Byte offset from the beginning of the file.</returns>
    public static long SequenceRegionOffset(uint slotCapacity)
    {
        return RowRegionOffset + RowRegionBytes(slotCapacity);
    }

    /// <summary>Gets the page-aligned byte length of the sequence-number region.</summary>
    /// <param name="slotCapacity">Number of rows allocated in the block.</param>
    /// <returns>Page-aligned sequence-region length in bytes.</returns>
    internal static long SequenceRegionBytes(uint slotCapacity)
    {
        return AlignUp((long)slotCapacity * SequenceBytes, PageSize);
    }

    /// <summary>Gets the byte offset of the name entry region, which follows the sequence region.</summary>
    /// <param name="slotCapacity">Number of rows allocated in the block.</param>
    /// <returns>Byte offset from the beginning of the file.</returns>
    internal static long NameEntryRegionOffset(uint slotCapacity)
    {
        return SequenceRegionOffset(slotCapacity) + SequenceRegionBytes(slotCapacity);
    }

    /// <summary>Gets the page-aligned length of the entry region, including reserved entry zero.</summary>
    /// <param name="entryCapacity">Number of allocatable name entries.</param>
    internal static long NameEntryRegionBytes(uint entryCapacity)
    {
        return AlignUp(((long)entryCapacity + 1) * NameEntry.Bytes, PageSize);
    }

    /// <summary>Gets the byte offset where the bucket heads begin, after the entries.</summary>
    internal static long BucketRegionOffset(uint slotCapacity, uint entryCapacity)
    {
        return NameEntryRegionOffset(slotCapacity) + NameEntryRegionBytes(entryCapacity);
    }

    /// <summary>Gets the page-aligned length of the bucket region.</summary>
    internal static long BucketRegionBytes(uint entryCapacity)
    {
        return AlignUp((long)ComputeBucketCount(entryCapacity) * sizeof(uint), PageSize);
    }

    /// <summary>A power of two at or above <c>max(1024, ceil(entries / 4))</c>.</summary>
    internal static uint ComputeBucketCount(uint entryCapacity)
    {
        var requested = Math.Max(MinimumBucketCount, (uint)(((ulong)entryCapacity + EntriesPerBucket - 1) / EntriesPerBucket));
        return System.Numerics.BitOperations.RoundUpToPowerOf2(requested);
    }

    /// <summary>Gets the byte offset at which the ASCII and UTF-16LE byte pool begins.</summary>
    /// <param name="slotCapacity">Number of rows allocated in the block.</param>
    /// <param name="entryCapacity">Number of allocatable name entries.</param>
    /// <returns>Byte offset from the beginning of the file.</returns>
    public static long NamePoolOffset(uint slotCapacity, uint entryCapacity)
    {
        return BucketRegionOffset(slotCapacity, entryCapacity) + BucketRegionBytes(entryCapacity);
    }

    /// <summary>Gets the total page-aligned block-file length required for the supplied capacities.</summary>
    /// <param name="slotCapacity">Number of rows allocated in the block.</param>
    /// <param name="entryCapacity">Number of allocatable name entries.</param>
    /// <param name="namePoolCapacity">Name byte-pool capacity in bytes.</param>
    /// <returns>Total required block-file length in bytes.</returns>
    public static long TotalBlockBytes(uint slotCapacity, uint entryCapacity, uint namePoolCapacity)
    {
        return NamePoolOffset(slotCapacity, entryCapacity) + AlignUp(namePoolCapacity, PageSize);
    }
}
