namespace MFTLib.Index;

/// <summary>
///     Byte geometry of a packed index block. One 4 KB header page, a dense row region of
///     32-byte rows indexed by record number, then an append-only UTF-16 name pool. Every
///     region boundary is 4 KB aligned so a mapped view can be paged independently.
/// </summary>
internal static class BlockLayout
{
    /// <summary>The ASCII bytes 'M', 'L', 'I', 'X' read as a little-endian unsigned 32-bit value.</summary>
    public const uint Magic = 0x58494C4D;

    /// <summary>A mismatch means discard the block and rescan. There is no migration path.</summary>
    public const uint FormatVersion = 3;

    /// <summary>Alignment, in bytes, of each on-disk block region.</summary>
    public const int PageSize = 4096;

    /// <summary>Bytes actually occupied by header fields. The header region is padded to a page.</summary>
    public const int HeaderFieldBytes = 112;

    /// <summary>Total header-region size in bytes, including padding.</summary>
    public const int HeaderRegionBytes = PageSize;

    /// <summary>Size, in bytes, of one fixed-size row.</summary>
    public const int RowBytes = 32;

    /// <summary>Headroom is 25 percent of the estimate or this many rows, whichever is larger.</summary>
    internal const int MinimumSlotHeadroomRows = 65536;

    /// <summary>Name pool headroom is 25 percent of the estimate or this many bytes, whichever is larger.</summary>
    internal const int MinimumNamePoolHeadroomBytes = 1048576;

    /// <summary>Mirrors the native resolver's cap so a corrupt parent column cannot loop forever.</summary>
    public const int MaximumPathDepth = 128;

    /// <summary>Size, in bytes, of one stored NTFS sequence number.</summary>
    internal const int SequenceBytes = 2;

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

    /// <summary>Computes name-pool capacity by adding the required headroom to an estimated byte count.</summary>
    /// <param name="estimatedNameBytes">Estimated UTF-16 name-pool size in bytes.</param>
    /// <returns>The estimate plus headroom of 25 percent of the estimate or <see cref="MinimumNamePoolHeadroomBytes" /> bytes, whichever is larger.</returns>
    /// <exception cref="OverflowException">The estimate plus required headroom exceeds <see cref="UInt32.MaxValue" />.</exception>
    public static uint ComputeNamePoolCapacity(uint estimatedNameBytes)
    {
        var headroom = Math.Max(estimatedNameBytes / 4, MinimumNamePoolHeadroomBytes);
        return checked(estimatedNameBytes + headroom);
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

    /// <summary>Gets the byte offset at which the UTF-16 name pool begins.</summary>
    /// <param name="slotCapacity">Number of rows allocated in the block.</param>
    /// <returns>Byte offset from the beginning of the file.</returns>
    public static long NamePoolOffset(uint slotCapacity)
    {
        return SequenceRegionOffset(slotCapacity) + SequenceRegionBytes(slotCapacity);
    }

    /// <summary>Gets the total page-aligned block-file length required for the supplied capacities.</summary>
    /// <param name="slotCapacity">Number of rows allocated in the block.</param>
    /// <param name="namePoolCapacity">UTF-16 name-pool capacity in bytes.</param>
    /// <returns>Total required block-file length in bytes.</returns>
    public static long TotalBlockBytes(uint slotCapacity, uint namePoolCapacity)
    {
        return NamePoolOffset(slotCapacity) + AlignUp(namePoolCapacity, PageSize);
    }
}
