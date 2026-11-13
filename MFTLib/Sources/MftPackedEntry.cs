using System.Runtime.CompilerServices;

namespace MFTLib;

/// <summary>
///     Reads one packed <c>MftCompactEntry</c> in place, at the offsets <c>mft_api.h</c> pins, so a slice is written
///     without materializing an <see cref="MftRecord" />. The pointer stays valid while its slice is pinned.
/// </summary>
readonly unsafe struct MftPackedEntry(byte* row)
{
    const ushort InUseFlag = 1;
    const ushort DirectoryFlag = 2;
    const ushort SizeUnknownFlag = 0x8000;

    internal ulong RecordNumber => Unsafe.ReadUnaligned<ulong>(row);

    internal ulong ParentRecordNumber => Unsafe.ReadUnaligned<ulong>(row + 8);

    internal ulong StringOffset => Unsafe.ReadUnaligned<ulong>(row + 16);

    internal uint FileAttributes => Unsafe.ReadUnaligned<uint>(row + 24);

    internal ushort StringLength => Unsafe.ReadUnaligned<ushort>(row + 30);

    internal long Size => Unsafe.ReadUnaligned<long>(row + 32);

    internal long ModifiedFileTime => Unsafe.ReadUnaligned<long>(row + 40);

    internal ushort SequenceNumber => Unsafe.ReadUnaligned<ushort>(row + 48);

    internal ushort ParentSequenceNumber => Unsafe.ReadUnaligned<ushort>(row + 50);

    internal bool InUse => (Flags & InUseFlag) != 0;

    internal bool IsDirectory => (Flags & DirectoryFlag) != 0;

    internal bool SizeKnown => (Flags & SizeUnknownFlag) == 0;

    internal ushort Flags => Unsafe.ReadUnaligned<ushort>(row + 28);
}
