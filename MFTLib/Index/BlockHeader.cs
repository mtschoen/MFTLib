using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace MFTLib.Index;

/// <summary>
///     The single header page of a block, laid out explicitly so the on-disk bytes are
///     independent of the runtime's field packing. Field order matches the format
///     specification. <see cref="RootRow" /> identifies the volume root in the dense row
///     region: zero for enumeration blocks and the NTFS root record for MFT blocks.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = BlockLayout.HeaderFieldBytes)]
public struct BlockHeader
{
    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public uint FormatVersion;
    [FieldOffset(8)] public ProducerKind ProducerKind;
    [FieldOffset(12)] public BlockFlags Flags;
    [FieldOffset(16)] public uint VolumeSerial;
    [FieldOffset(20)] public uint RootRow;
    [FieldOffset(24)] public long ScanTimestampTicks;
    [FieldOffset(32)] public uint RowCount;
    [FieldOffset(36)] public uint SlotCapacity;
    [FieldOffset(40)] public uint NamePoolUsed;
    [FieldOffset(44)] public uint NamePoolCapacity;
    [FieldOffset(48)] public ulong UsnJournalId;
    [FieldOffset(56)] public long UsnNextUsn;
    [FieldOffset(64)] public ulong Generation;
    [FieldOffset(72)] public ulong RowRegionOffset;
    [FieldOffset(80)] public ulong NamePoolOffset;

    public readonly bool IsComplete => (Flags & BlockFlags.Complete) != 0;

    public readonly bool IsCompactionNeeded => (Flags & BlockFlags.CompactionNeeded) != 0;

    public readonly DateTime ScanTimestampUtc => new(ScanTimestampTicks, DateTimeKind.Utc);

    /// <summary>
    ///     Decides whether a block file found on disk can be mapped and trusted. Order matters:
    ///     magic before version, because a file that is not a block at all would otherwise be
    ///     reported as a version mismatch.
    /// </summary>
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
            header.NamePoolOffset != (ulong)BlockLayout.NamePoolOffset(header.SlotCapacity))
        {
            return BlockValidationResult.InconsistentRegions;
        }

        var required = BlockLayout.TotalBlockBytes(header.SlotCapacity, header.NamePoolCapacity);
        return fileLength < required ? BlockValidationResult.InconsistentRegions : BlockValidationResult.Valid;
    }
}
