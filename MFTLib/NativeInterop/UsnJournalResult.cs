using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
struct UsnJournalResultNative
{
    public ulong EntryCount;
    public IntPtr Entries; // UsnJournalEntry*, owned by native side
    public long NextUsn;
    public ulong JournalIdentifier;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string ErrorMessage;
}
