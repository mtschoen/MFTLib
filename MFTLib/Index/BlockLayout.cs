namespace MFTLib.Index;

/// <summary>
///     Byte geometry of a packed index block: one 4 KB header page, a dense region of
///     32-byte rows indexed by record number, a per-row NTFS sequence-number region,
///     12-byte interned name entries, a hash bucket directory, the per-name posting regions
///     (offsets, rows, delta heads and delta entries) and an append-only ASCII and UTF-16LE byte
///     pool. Every region boundary is 4 KB aligned so a mapped view can be paged independently.
///     Entry-dependent regions are sized by the entry capacity E, which the header stores
///     separately from the slot capacity S; the delta entries are sized by the delta capacity D.
/// </summary>
static class BlockLayout
{
    /// <summary>The ASCII bytes 'M', 'L', 'I', 'X' read as a little-endian unsigned 32-bit value.</summary>
    public const uint Magic = 0x58494C4D;

    /// <summary>A mismatch means discard the block and rescan. There is no migration path.</summary>
    public const uint FormatVersion = 9;

    /// <summary>Alignment, in bytes, of each on-disk block region.</summary>
    public const int PageSize = 4096;

    /// <summary>Bytes actually occupied by header fields. The header region is padded to a page.</summary>
    public const int HeaderFieldBytes = 168;

    /// <summary>Total header-region size in bytes, including padding.</summary>
    public const int HeaderRegionBytes = PageSize;

    /// <summary>Size, in bytes, of one fixed-size row.</summary>
    public const int RowBytes = 32;

    /// <summary>Headroom is 25 percent of the estimate or this many rows, whichever is larger.</summary>
    const int MinimumSlotHeadroomRows = 65536;

    /// <summary>Largest estimate whose slot headroom still produces a creatable block.</summary>
    internal const uint MaximumEstimatedRowCount = 1717986917;

    /// <summary>Pool bytes left after a refusal of any NTFS-length name.</summary>
    internal const uint NamePoolExhaustionMarginBytes = 510;

    /// <summary>Floor of the pool reservation, so a small volume still has room for renames.</summary>
    const long MinimumNamePoolBytes = 1048576;

    /// <summary>Reserved per slot; measured use is 2.7 to 15.2 bytes per slot, so this is generous headroom.</summary>
    const long NamePoolBytesPerSlot = 24;

    /// <summary>Page-aligned ceiling under 2 GiB, so a pool span's length always fits an int.</summary>
    internal const uint MaximumNamePoolCapacity = 0x7FFFF000;

    /// <summary>Slots up to this count get one name entry each: distinct names reach 61 percent of records on small volumes.</summary>
    const uint FullNameEntrySlots = 2_000_000;

    /// <summary>Beyond <see cref="FullNameEntrySlots" /> one name entry is planned per this many slots (measured 9 to 35 percent).</summary>
    const uint SlotsPerTailNameEntry = 5;

    /// <summary>Floor of the bucket directory, so a tiny block still spreads its chains.</summary>
    const uint MinimumBucketCount = 1024;

    /// <summary>Target chain length: one head per this many entries before rounding up to a power of two.</summary>
    const uint EntriesPerBucket = 4;

    /// <summary>Floor of the delta entry capacity, so a small block can still absorb journal name changes.</summary>
    const uint MinimumDeltaCapacity = 65536;

    /// <summary>Delta entries are planned as one per this many slots, rounded up.</summary>
    const uint SlotsPerDeltaEntry = 8;

    /// <summary>Largest name entry capacity: the posting offsets region holds one word per entry plus two, int-indexed.</summary>
    internal const uint MaximumNameEntryCapacity = int.MaxValue - 2;

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

    /// <summary>Size, in bytes, of one posting offset and one posting row index.</summary>
    const int PostingWordBytes = sizeof(uint);

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
    /// <exception cref="OverflowException">The estimate plus required headroom exceeds <see cref="uint.MaxValue" />.</exception>
    public static uint ComputeSlotCapacity(uint estimatedRowCount)
    {
        var headroom = Math.Max(estimatedRowCount / 4, MinimumSlotHeadroomRows);
        return checked(estimatedRowCount + headroom);
    }

    /// <summary>
    ///     Clamps the estimate, computes the volume plan (`S`, then `E`, `P` and `D` from `S`), then raises
    ///     each region independently to its required minimum.
    /// </summary>
    /// <param name="estimatedRowCount">Rows the producer estimates, clamped to the largest creatable plan.</param>
    /// <param name="minimum">Regional minima from the block being replaced; <see langword="default" /> asks for the volume plan.</param>
    /// <returns>The four capacities of the replacement block.</returns>
    internal static BlockCapacities PlanCapacities(uint estimatedRowCount, BlockCapacities minimum)
    {
        var slotCapacity = ComputeSlotCapacity(Math.Min(estimatedRowCount, MaximumEstimatedRowCount));
        var plan = new BlockCapacities(slotCapacity, ComputeNameEntryCapacity(slotCapacity),
            ComputeNamePoolCapacity(slotCapacity), ComputeDeltaCapacity(slotCapacity));
        return plan.AtLeast(minimum);
    }

    /// <summary>
    ///     Returns no minimum when the capacity flag is clear; otherwise doubles each exhausted region,
    ///     using rows as the fallback when none of the entry, pool and delta regions reached its exhaustion test.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1242",
        Justification = "BlockHeader is explicit-layout and intentionally mutable for field-by-field disk mapping; the in-parameter signature is spec-mandated.")]
    internal static BlockCapacities GrowthMinimum(in BlockHeader header)
    {
        if (!header.IsCapacityExhausted)
        {
            return default;
        }

        var entriesExhausted = header.NameEntryCount >= header.NameEntryCapacity;
        var poolExhausted = header.NamePoolCapacity - header.NamePoolUsed <= NamePoolExhaustionMarginBytes;
        var deltaExhausted = header.DeltaEntryCount >= header.DeltaCapacity;
        var rowsExhausted = header.RowCount >= header.SlotCapacity ||
                            (!entriesExhausted && !poolExhausted && !deltaExhausted);
        return new BlockCapacities(
            rowsExhausted ? DoubleCapacity(header.SlotCapacity) : 0,
            entriesExhausted ? DoubleNameEntryCapacity(header.NameEntryCapacity) : 0,
            poolExhausted ? DoubleNamePoolCapacity(header.NamePoolCapacity) : 0,
            deltaExhausted ? DoubleCapacity(header.DeltaCapacity) : 0);
    }

    /// <summary>
    ///     The regions a producer that can re-run its scan must enlarge before it may publish a block whose name
    ///     entries or name pool ran out, or no minimum when the block is not capacity-exhausted or ran out of rows.
    ///     Entries jump to the slot capacity, which no scan can exceed because every row interns at most one name;
    ///     the pool doubles. Row exhaustion keeps its flag-and-rescan semantics and is never retried.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1242",
        Justification = "BlockHeader is explicit-layout and intentionally mutable for field-by-field disk mapping; the in-parameter signature is spec-mandated.")]
    internal static BlockCapacities RetryMinimum(in BlockHeader header)
    {
        if (!header.IsCapacityExhausted || header.RowCount >= header.SlotCapacity)
        {
            return default;
        }

        var entriesExhausted = header.NameEntryCount >= header.NameEntryCapacity;
        var poolExhausted = header.NamePoolCapacity - header.NamePoolUsed <= NamePoolExhaustionMarginBytes;
        return new BlockCapacities(0,
            entriesExhausted
                ? header.NameEntryCapacity < header.SlotCapacity ? header.SlotCapacity : DoubleCapacity(header.NameEntryCapacity)
                : 0,
            poolExhausted ? DoubleNamePoolCapacity(header.NamePoolCapacity) : 0);
    }

    /// <summary>
    ///     Whether a producer that can re-run its scan has a larger name region to retry with, so the running scan is
    ///     already doomed to be discarded and may stop; false at the clamps, where the flagged block is published.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Roslynator", "RCS1242",
        Justification = "BlockHeader is explicit-layout and intentionally mutable for field-by-field disk mapping; the in-parameter signature is spec-mandated.")]
    internal static bool RetryGrowsCapacity(in BlockHeader header)
    {
        var retry = RetryMinimum(in header);
        return retry.NameEntryCapacity > header.NameEntryCapacity || retry.NamePoolCapacity > header.NamePoolCapacity;
    }

    /// <summary>Doubles a capacity while leaving room for reserved name entry zero in an int-indexed span.</summary>
    /// <param name="capacity">The capacity to double.</param>
    /// <returns>Twice <paramref name="capacity" />, or the clamp when that is larger.</returns>
    internal static uint DoubleCapacity(uint capacity)
    {
        return (uint)Math.Min(capacity * 2UL, int.MaxValue - 1UL);
    }

    /// <summary>Doubles a name entry capacity, clamped to <see cref="MaximumNameEntryCapacity" />.</summary>
    internal static uint DoubleNameEntryCapacity(uint capacity)
    {
        return (uint)Math.Min(capacity * 2UL, MaximumNameEntryCapacity);
    }

    /// <summary>Doubles a name pool capacity, clamped to <see cref="MaximumNamePoolCapacity" />.</summary>
    internal static uint DoubleNamePoolCapacity(uint capacity)
    {
        return (uint)Math.Min(capacity * 2UL, MaximumNamePoolCapacity);
    }

    /// <summary>
    ///     Plans name entries for a slot capacity: one per slot up to <see cref="FullNameEntrySlots" />, then one per
    ///     <see cref="SlotsPerTailNameEntry" /> further slots. Interning leaves far fewer distinct names than rows on large
    ///     volumes; an exceeded plan sets the capacity-exhausted flag and the growth path doubles the entries.
    /// </summary>
    /// <param name="slotCapacity">Row slot capacity, headroom included.</param>
    /// <returns>The allocatable name entry count, never above <paramref name="slotCapacity" />.</returns>
    internal static uint ComputeNameEntryCapacity(uint slotCapacity)
    {
        return slotCapacity <= FullNameEntrySlots
            ? slotCapacity
            : FullNameEntrySlots + (slotCapacity - FullNameEntrySlots) / SlotsPerTailNameEntry;
    }

    /// <summary>
    ///     Plans delta entries for a slot capacity: one per <see cref="SlotsPerDeltaEntry" /> slots, at least
    ///     <see cref="MinimumDeltaCapacity" />. Every journal name change consumes one; an exceeded plan sets the
    ///     capacity-exhausted flag and the growth path doubles the delta region.
    /// </summary>
    /// <param name="slotCapacity">Row slot capacity, headroom included.</param>
    /// <returns>The allocatable delta entry count, excluding reserved entry zero.</returns>
    internal static uint ComputeDeltaCapacity(uint slotCapacity)
    {
        var proportional = (uint)(((ulong)slotCapacity + SlotsPerDeltaEntry - 1) / SlotsPerDeltaEntry);
        return Math.Max(MinimumDeltaCapacity, proportional);
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

    /// <summary>Gets the byte offset of the posting offsets, which follow the bucket heads.</summary>
    internal static long PostingOffsetRegionOffset(uint slotCapacity, uint entryCapacity)
    {
        return BucketRegionOffset(slotCapacity, entryCapacity) + BucketRegionBytes(entryCapacity);
    }

    /// <summary>Gets the page-aligned length of the posting offsets: one word per name plus the reserved zero and the end.</summary>
    internal static long PostingOffsetRegionBytes(uint entryCapacity)
    {
        return AlignUp(((long)entryCapacity + 2) * PostingWordBytes, PageSize);
    }

    /// <summary>Gets the byte offset of the posting rows, which follow the posting offsets.</summary>
    internal static long PostingRowRegionOffset(uint slotCapacity, uint entryCapacity)
    {
        return PostingOffsetRegionOffset(slotCapacity, entryCapacity) + PostingOffsetRegionBytes(entryCapacity);
    }

    /// <summary>Gets the page-aligned length of the posting rows: at most one row index per slot.</summary>
    internal static long PostingRowRegionBytes(uint slotCapacity)
    {
        return AlignUp((long)slotCapacity * PostingWordBytes, PageSize);
    }

    /// <summary>Gets the byte offset of the delta heads, which follow the posting rows.</summary>
    internal static long DeltaHeadRegionOffset(uint slotCapacity, uint entryCapacity)
    {
        return PostingRowRegionOffset(slotCapacity, entryCapacity) + PostingRowRegionBytes(slotCapacity);
    }

    /// <summary>Gets the page-aligned length of the delta heads, one per name bucket.</summary>
    internal static long DeltaHeadRegionBytes(uint entryCapacity)
    {
        return AlignUp((long)ComputeBucketCount(entryCapacity) * sizeof(uint), PageSize);
    }

    /// <summary>Gets the byte offset of the delta entries, which follow the delta heads.</summary>
    internal static long DeltaEntryRegionOffset(uint slotCapacity, uint entryCapacity)
    {
        return DeltaHeadRegionOffset(slotCapacity, entryCapacity) + DeltaHeadRegionBytes(entryCapacity);
    }

    /// <summary>Gets the page-aligned length of the delta entries, including reserved entry zero.</summary>
    internal static long DeltaEntryRegionBytes(uint deltaCapacity)
    {
        return AlignUp(((long)deltaCapacity + 1) * DeltaEntry.Bytes, PageSize);
    }

    /// <summary>Gets the byte offset at which the ASCII and UTF-16LE byte pool begins.</summary>
    /// <param name="slotCapacity">Number of rows allocated in the block.</param>
    /// <param name="entryCapacity">Number of allocatable name entries.</param>
    /// <param name="deltaCapacity">Number of allocatable delta entries.</param>
    /// <returns>Byte offset from the beginning of the file.</returns>
    public static long NamePoolOffset(uint slotCapacity, uint entryCapacity, uint deltaCapacity)
    {
        return DeltaEntryRegionOffset(slotCapacity, entryCapacity) + DeltaEntryRegionBytes(deltaCapacity);
    }

    /// <summary>Gets the total page-aligned block-file length required for the supplied capacities.</summary>
    /// <param name="slotCapacity">Number of rows allocated in the block.</param>
    /// <param name="entryCapacity">Number of allocatable name entries.</param>
    /// <param name="deltaCapacity">Number of allocatable delta entries.</param>
    /// <param name="namePoolCapacity">Name byte-pool capacity in bytes.</param>
    /// <returns>Total required block-file length in bytes.</returns>
    public static long TotalBlockBytes(uint slotCapacity, uint entryCapacity, uint deltaCapacity, uint namePoolCapacity)
    {
        return NamePoolOffset(slotCapacity, entryCapacity, deltaCapacity) + AlignUp(namePoolCapacity, PageSize);
    }
}
