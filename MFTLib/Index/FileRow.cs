using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MFTLib.Index;

/// <summary>
///     One 32-byte row in the row region. Rows are dense by record number, so row i is
///     record i and no lookup table exists. Explicit layout keeps the on-disk bytes fixed.
///     The name offset, name length, and flags sit together at byte offset 8 so they form a
///     single 8-byte aligned descriptor word: the row region starts at
///     <see cref="BlockLayout.RowRegionOffset" /> and rows are <see cref="BlockLayout.RowBytes" />
///     apart, both multiples of 8, so byte 8 of every row is 8-byte aligned. That lets a rename
///     publish a new name with one atomic store instead of two independent ones.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = BlockLayout.RowBytes)]
public struct FileRow
{
    /// <summary>Row index of the parent directory. The volume root points at itself.</summary>
    [FieldOffset(0)] public uint ParentRow;

    /// <summary>Raw NTFS file attributes, castable to <see cref="System.IO.FileAttributes" />.</summary>
    [FieldOffset(4)] public uint Attributes;

    /// <summary>Byte offset of the name inside the name pool. Low half of the descriptor word.</summary>
    [FieldOffset(8)] public uint NameOffsetBytes;

    /// <summary>Name length in UTF-16 code units, not bytes.</summary>
    [FieldOffset(12)] public ushort NameLengthUnits;

    [FieldOffset(14)] public RowFlags Flags;

    /// <summary>Size in bytes. Directories carry zero, as do rows with the size-unknown flag.</summary>
    [FieldOffset(16)] public long Size;

    [FieldOffset(24)] public long ModifiedTicks;

    public readonly bool IsInUse => (Flags & RowFlags.InUse) != 0;

    public readonly bool IsDirectory => (Flags & RowFlags.Directory) != 0;

    public readonly bool IsDeleted => (Flags & RowFlags.Tombstone) != 0;

    public readonly bool SizeKnown => (Flags & RowFlags.SizeUnknown) == 0;

    public readonly bool SubtreeSkipped => (Flags & RowFlags.SubtreeSkipped) != 0;

    public readonly DateTime ModifiedUtc => new(ModifiedTicks, DateTimeKind.Utc);

    /// <summary>
    ///     Reads the name offset, name length, and flags as one 64-bit value. The read is atomic
    ///     because the word is 8-byte aligned and MFTLib targets x64 only, where ECMA-335
    ///     guarantees that aligned reads and writes no wider than a native word are atomic.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Single writer, many readers. Exactly one thread mutates a given block, so a reader
    ///         never has to resolve competing writes; it only has to avoid observing half of one.
    ///     </para>
    ///     <para>
    ///         The exact invariant, which is narrower than "read everything through here": the
    ///         name offset and the name length must come from one call to this method on a live
    ///         block. Never pair a direct <see cref="NameOffsetBytes" /> read with a direct
    ///         <see cref="NameLengthUnits" /> read, because two separate field reads can straddle
    ///         a concurrent rename and pair a new offset with an old length. That is the torn read
    ///         this layout exists to prevent.
    ///     </para>
    ///     <para>
    ///         The flags may be read alone, and the scan loops do exactly that through
    ///         <see cref="IsInUse" />, <see cref="IsDirectory" />, <see cref="IsDeleted" />,
    ///         <see cref="SizeKnown" /> and <see cref="SubtreeSkipped" />. The field is 2-byte
    ///         aligned so it cannot tear on its own, and pairing such a read with a separate name
    ///         read is safe because the name pool is append-only: a span built from a descriptor
    ///         that is one rename stale still points at valid, immutable characters. Routing the
    ///         predicates through this method would cost a 64-bit read plus three shifts per row
    ///         on the hottest loop in the library and buy no correctness.
    ///     </para>
    ///     <para>
    ///         Writes have no such latitude: every write goes through
    ///         <see cref="WriteDescriptorWord" />, including one that only means to change flags.
    ///     </para>
    /// </remarks>
    internal static ulong ReadDescriptorWord(ref readonly FileRow row)
    {
        return Volatile.Read(ref Unsafe.As<uint, ulong>(ref Unsafe.AsRef(in row).NameOffsetBytes));
    }

    /// <summary>
    ///     Publishes the name offset, name length, and flags as one atomic store, so a concurrent
    ///     reader observes the whole descriptor before the change or the whole descriptor after
    ///     it. Callers that only mean to change the flags must still round-trip the other two
    ///     values through here, otherwise a flags update would tear against a rename. The layout
    ///     is little-endian, matching the on-disk block format.
    /// </summary>
    internal static void WriteDescriptorWord(ref FileRow row, uint nameOffsetBytes,
        ushort nameLengthUnits, RowFlags flags)
    {
        var word = nameOffsetBytes | ((ulong)nameLengthUnits << 32) | ((ulong)(ushort)flags << 48);
        Volatile.Write(ref Unsafe.As<uint, ulong>(ref row.NameOffsetBytes), word);
    }

    internal static uint DescriptorNameOffsetBytes(ulong descriptorWord)
    {
        return (uint)descriptorWord;
    }

    internal static ushort DescriptorNameLengthUnits(ulong descriptorWord)
    {
        return (ushort)(descriptorWord >> 32);
    }

    internal static RowFlags DescriptorFlags(ulong descriptorWord)
    {
        return (RowFlags)(ushort)(descriptorWord >> 48);
    }
}
