using System.Runtime.InteropServices;

namespace MFTLib.Interop;

[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
struct UsnJournalInfoNative
{
    public ulong JournalIdentifier;
    public long FirstUsn;
    public long NextUsn;
    public long LowestValidUsn;
    public long MaximumUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string ErrorMessage;
}
