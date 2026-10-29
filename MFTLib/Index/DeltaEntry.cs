using System.Runtime.InteropServices;

namespace MFTLib.Index;

/// <summary>
///     One journal-added association of a row with a name identifier, chained newest-first through
///     <see cref="Next" />. Entry zero is reserved: a zero head is an empty chain, and an all-zero entry names no
///     name and ends the walk.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Bytes)]
struct DeltaEntry
{
    internal const int Bytes = 12;
    [FieldOffset(0)] public uint Row;
    [FieldOffset(4)] public uint NameId;
    [FieldOffset(8)] public uint Next;
}
