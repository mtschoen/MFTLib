using System.Runtime.InteropServices;

namespace MFTLib.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
internal struct MftFileEntry
{
    public ulong RecordNumber;
    public ulong ParentRecordNumber;
    public ushort Flags;           // bit 0 = in use, bit 1 = directory
    public ushort FileNameLength;  // wchar_t count (excluding null terminator)

    // Fixed-size inline char array: 260 wchar_t (MAX_PATH)
    // Marshal as ByValTStr to match the C++ wchar_t[260]
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string FileName;

    public bool InUse => (Flags & 1) != 0;
    public bool IsDirectory => (Flags & 2) != 0;
}
