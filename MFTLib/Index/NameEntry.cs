using System.Runtime.InteropServices;

namespace MFTLib.Index;

/// <summary>An immutable interned name descriptor. Entry zero is reserved for no name.</summary>
[StructLayout(LayoutKind.Explicit, Size = Bytes)]
struct NameEntry
{
    internal const int Bytes = 12;
    [FieldOffset(0)] public uint PoolOffsetBytes;
    [FieldOffset(4)] public ushort LengthAndEncoding;
    [FieldOffset(6)] public ushort HashTag;
    [FieldOffset(8)] public uint Next;

    internal readonly int Length => LengthAndEncoding & 0x7FFF;
    internal readonly bool IsUtf16 => (LengthAndEncoding & 0x8000) != 0;
}
