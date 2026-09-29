using System.Runtime.InteropServices;

namespace MFTLib.Interop;

// Mirrors the native MftParseControl: caller-owned, kept in place for the whole parse call,
// written by the caller and only read by the parser.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct MftParseControl
{
    public int CancelRequested;
    public int ParseThreadAllowance;
}
