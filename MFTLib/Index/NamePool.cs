namespace MFTLib.Index;

/// <summary>
///     The append-only UTF-16 name region of a block. A rename appends the new name and then
///     swaps the row's offset and length, so a reader sees the old name or the new one and
///     never a torn one. Names are not interned in v1.
/// </summary>
public static class NamePool
{
    /// <summary>The row's name length field is 16 bits, so a name longer than this cannot be stored.</summary>
    internal const int MaximumNameLengthUnits = 32767;

    /// <summary>Returns a name slice addressed by a byte offset and UTF-16 code-unit length.</summary>
    /// <param name="pool">Name-pool characters.</param>
    /// <param name="offsetBytes">Byte offset of the name in <paramref name="pool" />.</param>
    /// <param name="lengthUnits">Name length in UTF-16 code units.</param>
    /// <returns>A view over the requested characters; the caller must keep the backing block alive.</returns>
    public static ReadOnlySpan<char> Read(ReadOnlySpan<char> pool, uint offsetBytes, ushort lengthUnits)
    {
        var start = (int)(offsetBytes / sizeof(char));
        return pool.Slice(start, lengthUnits);
    }

    /// <summary>
    ///     Reads one row's name. The offset and the length come from a single descriptor-word
    ///     read, so a rename running concurrently on the writer thread cannot hand this reader
    ///     one name's offset paired with another name's length.
    /// </summary>
    /// <param name="block">Mapped block containing the row and name pool.</param>
    /// <param name="rowIndex">Row whose name is read.</param>
    /// <returns>A view over the row name; it becomes unusable when <paramref name="block" /> is disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="block" /> is null.</exception>
    public static ReadOnlySpan<char> ReadRowName(BlockFile block, uint rowIndex)
    {
        ArgumentNullException.ThrowIfNull(block);
        var descriptor = FileRow.ReadDescriptorWord(in block.Rows[(int)rowIndex]);
        return Read(block.NamePoolCharacters, FileRow.DescriptorNameOffsetBytes(descriptor),
            FileRow.DescriptorNameLengthUnits(descriptor));
    }

    /// <summary>
    ///     Appends a name and reports where it landed. Returns false when the name does not fit
    ///     or is longer than the 16-bit length field allows, leaving <paramref name="usedBytes" />
    ///     untouched. A false result is the pool-exhaustion path: the caller sets the
    ///     compaction-needed flag and keeps applying what does fit.
    /// </summary>
    /// <param name="pool">Writable UTF-16 name-pool storage.</param>
    /// <param name="usedBytes">Used pool bytes, updated only after a successful append.</param>
    /// <param name="capacityBytes">Usable pool capacity in bytes.</param>
    /// <param name="name">Name to append.</param>
    /// <param name="offsetBytes">Receives the appended name's byte offset, or zero on failure.</param>
    /// <returns>true when the name fits and was appended; otherwise, false.</returns>
    public static bool TryAppend(Span<char> pool, ref uint usedBytes, uint capacityBytes,
        ReadOnlySpan<char> name, out uint offsetBytes)
    {
        offsetBytes = 0;
        if (name.Length > MaximumNameLengthUnits)
        {
            return false;
        }

        var requiredBytes = (uint)name.Length * sizeof(char);
        if (usedBytes > capacityBytes || capacityBytes - usedBytes < requiredBytes)
        {
            return false;
        }

        // Widened deliberately. The capacity guard above already keeps the sum inside a signed
        // 32-bit range today, but that is an inherited property of two other bounds rather than
        // anything this line states, and a pool near the top of the 32-bit byte range leaves no
        // headroom at all. Bounding in 64-bit arithmetic before narrowing makes the guarantee
        // local: this method reports "does not fit" by returning false, never by throwing.
        var start = (long)usedBytes / sizeof(char);
        if (start + name.Length > pool.Length)
        {
            return false;
        }

        name.CopyTo(pool.Slice((int)start, name.Length));
        offsetBytes = usedBytes;
        usedBytes += requiredBytes;
        return true;
    }
}
