namespace MFTLib.Index;

/// <summary>
///     Byte geometry of a packed index block. One 4 KB header page, a dense row region of
///     32-byte rows indexed by record number, then an append-only UTF-16 name pool. Every
///     region boundary is 4 KB aligned so a mapped view can be paged independently.
/// </summary>
public static class BlockLayout
{
    /// <summary>The ASCII bytes 'M', 'L', 'I', 'X' read as a little-endian unsigned 32-bit value.</summary>
    public const uint Magic = 0x58494C4D;

    /// <summary>A mismatch means discard the block and rescan. There is no migration path.</summary>
    public const uint FormatVersion = 1;

    public const int PageSize = 4096;

    /// <summary>Bytes actually occupied by header fields. The header region is padded to a page.</summary>
    public const int HeaderFieldBytes = 88;

    public const int HeaderRegionBytes = PageSize;

    public const int RowBytes = 32;

    /// <summary>Headroom is 25 percent of the estimate or this many rows, whichever is larger.</summary>
    public const int MinimumSlotHeadroomRows = 65536;

    /// <summary>Name pool headroom is 25 percent of the estimate or this many bytes, whichever is larger.</summary>
    public const int MinimumNamePoolHeadroomBytes = 1048576;

    /// <summary>Mirrors the native resolver's cap so a corrupt parent column cannot loop forever.</summary>
    public const int MaximumPathDepth = 128;

    /// <summary>
    ///     Fixed, not computed: the header region is exactly one page, so this can never be
    ///     anything other than <see cref="HeaderRegionBytes" />.
    /// </summary>
    public const long RowRegionOffset = HeaderRegionBytes;

    public static long AlignUp(long value, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    public static uint ComputeSlotCapacity(uint estimatedRowCount)
    {
        var headroom = Math.Max(estimatedRowCount / 4, MinimumSlotHeadroomRows);
        return checked(estimatedRowCount + headroom);
    }

    public static uint ComputeNamePoolCapacity(uint estimatedNameBytes)
    {
        var headroom = Math.Max(estimatedNameBytes / 4, MinimumNamePoolHeadroomBytes);
        return checked(estimatedNameBytes + headroom);
    }

    public static long RowRegionBytes(uint slotCapacity)
    {
        return AlignUp((long)slotCapacity * RowBytes, PageSize);
    }

    public static long NamePoolOffset(uint slotCapacity)
    {
        return RowRegionOffset + RowRegionBytes(slotCapacity);
    }

    public static long TotalBlockBytes(uint slotCapacity, uint namePoolCapacity)
    {
        return NamePoolOffset(slotCapacity) + AlignUp(namePoolCapacity, PageSize);
    }
}
