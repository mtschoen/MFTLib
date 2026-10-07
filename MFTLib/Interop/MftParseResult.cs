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

    public IntPtr EntryStrings; // ushort*
    public ulong EntryStringUnits;
    public uint AbiVersion;
    public uint EntryStride;
    public uint Cancelled; // 1 when MftParseControl.CancelRequested stopped the parse
    public uint InvalidInput; // 1 when a file input's content was rejected; ErrorMessage says why
    public ulong InvalidFixupRecords; // allocated records a volume scan passed over for an invalid fixup
}
