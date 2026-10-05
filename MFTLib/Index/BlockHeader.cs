using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MFTLib.Index;

/// <summary>
///     The single header page of a block, laid out explicitly so the on-disk bytes are
///     independent of the runtime's field packing. Field order matches the format
///     specification; <see cref="RootRow" /> aligns the 64-bit fields that follow.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = BlockLayout.HeaderFieldBytes)]
internal struct BlockHeader
{
    /// <summary>Format magic used to recognize a MFTLib index block.</summary>
    [FieldOffset(0)] public uint Magic;
    /// <summary>On-disk block-format version.</summary>
    [FieldOffset(4)] public uint FormatVersion;
    /// <summary>Kind of producer that populated the block.</summary>
    [FieldOffset(8)] public ProducerKind ProducerKind;
    /// <summary>Completion and compaction state published by the producer.</summary>
    [FieldOffset(12)] public BlockFlags Flags;
    /// <summary>NTFS serial number of the volume represented by the block.</summary>
    [FieldOffset(16)] public uint VolumeSerial;

    /// <summary>
    ///     Row index of the volume root. An enumeration block writes its root at row 0, so
    ///     zero is the correct value there. An MFT block sets 5, the NTFS root directory
    ///     record, because record 0 is $MFT.
    /// </summary>
    [FieldOffset(20)] public uint RootRow;

    /// <summary>UTC scan completion time as <see cref="DateTime.Ticks" />.</summary>
    [FieldOffset(24)] public long ScanTimestampTicks;
    /// <summary>Highest allocated row index plus one, including tombstones and empty gaps.</summary>
    [FieldOffset(32)] public uint RowCount;
    /// <summary>Total number of fixed-size row slots in the block.</summary>
    [FieldOffset(36)] public uint SlotCapacity;
    /// <summary>Number of bytes currently occupied in the UTF-16 name pool.</summary>
    [FieldOffset(40)] public uint NamePoolUsed;
    /// <summary>Total capacity, in bytes, of the UTF-16 name pool.</summary>
    [FieldOffset(44)] public uint NamePoolCapacity;
    /// <summary>USN journal identifier captured for this block, or zero when no journal was read.</summary>
    [FieldOffset(48)] public ulong UsnJournalId;
    /// <summary>Next USN cursor captured for this block, or zero when no journal was read.</summary>
    [FieldOffset(56)] public long UsnNextUsn;
    /// <summary>
    ///     Monotonically increasing mutation generation, bumped once per journal batch that
    ///     changed at least one row: a reported create, delete, rename, or modification, or a
    ///     close record that restamped a live row's timestamp and attributes. Header-only writes
    ///     leave it unchanged: every batch advances the journal checkpoint
    ///     (<see cref="UsnJournalId" /> and <see cref="UsnNextUsn" />), and a rejection (record
    ///     number past slot capacity, name pool exhausted, or parent record number out of range)
    ///     sets the compaction-needed flag without bumping the generation. Producer completion
    ///     stamps 1 on a block no batch has mutated.
    /// </summary>
    [FieldOffset(64)] public ulong Generation;
    /// <summary>Byte offset of the fixed-size row region from the start of the file.</summary>
    [FieldOffset(72)] public ulong RowRegionOffset;
    /// <summary>Byte offset of the append-only UTF-16 name pool from the start of the file.</summary>
    [FieldOffset(80)] public ulong NamePoolOffset;
    /// <summary>
    ///     Rows that are in use and not tombstoned. <see cref="RowCount" /> is the highest used
    ///     slot plus one, so on a block whose rows are dense by record number it counts free
    ///     slots and deleted files too. Maintained by <see cref="BlockWriter" />, the only writer
    ///     of rows, so both producers and the journal mutator get it without their own bookkeeping.
    /// </summary>
    [FieldOffset(88)] public uint LiveRowCount;
    /// <summary>Byte offset of the per-row NTFS sequence-number region.</summary>
    [FieldOffset(96)] public ulong SequenceRegionOffset;
    /// <summary>Packed four-character consumer cache identity.</summary>
    [FieldOffset(104)] public uint CacheTagFourCc;
    /// <summary>Consumer-defined cache identity version.</summary>
    [FieldOffset(108)] public uint CacheTagVersion;
    /// <summary>Consumer cache identity reconstructed from its on-disk fields.</summary>
    public readonly CacheTag CacheTag => CacheTag.FromStorage(CacheTagFourCc, CacheTagVersion);

    /// <summary>Determines whether the producer marked the block complete.</summary>
    public readonly bool IsComplete => (Flags & BlockFlags.Complete) != 0;

    /// <summary>Determines whether a mutation exhausted the block's reserved capacity.</summary>
    public readonly bool IsCompactionNeeded => (Flags & BlockFlags.CompactionNeeded) != 0;

    /// <summary>Converts <see cref="ScanTimestampTicks" /> to a UTC <see cref="DateTime" />.</summary>
    public readonly DateTime ScanTimestampUtc => new(ScanTimestampTicks, DateTimeKind.Utc);

    /// <summary>
    ///     Decides whether a block file found on disk can be mapped and trusted. Order matters:
    ///     magic before version, because a file that is not a block at all would otherwise be
    ///     reported as a version mismatch.
    /// </summary>
    /// <param name="header">Header bytes read from the candidate block file.</param>
    /// <param name="expectedVolumeSerial">Serial number of the volume the caller intends to open.</param>
    /// <param name="fileLength">Candidate file length in bytes.</param>
    /// <returns>A validation result that explains whether the block is safe to use.</returns>
    [SuppressMessage("Roslynator", "RCS1242",
        Justification = "BlockHeader is explicit-layout and intentionally mutable for field-by-field disk mapping; the in-parameter signature is spec-mandated.")]
    public static BlockValidationResult Validate(in BlockHeader header, uint expectedVolumeSerial, long fileLength)
    {
        if (header.Magic != BlockLayout.Magic)
        {
            return BlockValidationResult.WrongMagic;
        }

        if (header.FormatVersion != BlockLayout.FormatVersion)
        {
            return BlockValidationResult.WrongFormatVersion;
        }

        if (!header.IsComplete)
        {
            return BlockValidationResult.Incomplete;
        }

        if (header.VolumeSerial != expectedVolumeSerial)
        {
            return BlockValidationResult.WrongVolumeSerial;
        }

        if (!CacheTag.IsValidStorage(header.CacheTagFourCc))
        {
            return BlockValidationResult.WrongCacheTag;
        }

        return ValidateRegions(in header, fileLength);
    }

    [SuppressMessage("Roslynator", "RCS1242",
        Justification = "BlockHeader is explicit-layout and intentionally mutable for field-by-field disk mapping; the in-parameter signature is spec-mandated.")]
    static BlockValidationResult ValidateRegions(in BlockHeader header, long fileLength)
    {
        if (header.RowCount > header.SlotCapacity || header.NamePoolUsed > header.NamePoolCapacity ||
            header.RootRow >= header.RowCount)
        {
            return BlockValidationResult.InconsistentRegions;
        }

        if (header.RowRegionOffset != BlockLayout.RowRegionOffset ||
            header.SequenceRegionOffset != (ulong)BlockLayout.SequenceRegionOffset(header.SlotCapacity) ||
            header.NamePoolOffset != (ulong)BlockLayout.NamePoolOffset(header.SlotCapacity))
        {
            return BlockValidationResult.InconsistentRegions;
        }

        var required = BlockLayout.TotalBlockBytes(header.SlotCapacity, header.NamePoolCapacity);
        return fileLength < required ? BlockValidationResult.InconsistentRegions : BlockValidationResult.Valid;
    }
}
