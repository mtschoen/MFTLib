using System.Runtime.InteropServices;

namespace MFTLib.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
internal struct MftParseResult
{
    public ulong TotalRecords;
    public ulong UsedRecords;
    public IntPtr Entries;  // MftFileEntry*, owned by native side

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string ErrorMessage;
}
