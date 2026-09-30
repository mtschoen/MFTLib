using System.Runtime.InteropServices;

namespace MFTLib.Interop;

// Mirrors the native MftParseControl: caller-owned, kept in place for the whole parse call,
// written by the caller and only read by the parser. Naturally aligned, like the native struct,
// because the parser reads both fields as shared 32-bit loads.
[StructLayout(LayoutKind.Sequential)]
struct MftParseControl
{
    public int CancelRequested;
    public int ParseThreadAllowance;
}
