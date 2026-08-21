using System.Runtime.InteropServices;

namespace MFTLib.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
struct MftParseResult
{
    public ulong TotalRecords;
    public ulong UsedRecords;
    public IntPtr Entries; // MftCompactEntry*, owned by native side

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string ErrorMessage;

    // Performance counters (milliseconds)
    public double IoTimeMs;
    public double FixupTimeMs;
    public double ParseTimeMs;
    public double TotalTimeMs;

    public IntPtr PathEntries; // MftCompactEntry*, set when path resolution is requested
    public IntPtr EntryStrings; // ushort*
    public ulong EntryStringUnits;
    public IntPtr PathStrings; // ushort*
    public ulong PathStringUnits;
    public uint AbiVersion;
    public uint EntryStride;
}
