using System.Runtime.InteropServices;

namespace MFTLib.Interop;

/// <summary>
///     What the native open of a dump found. <see cref="ErrorMessage" /> is empty exactly when the
///     open returned an input.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
struct MftDumpInputInfo
{
    public ulong LengthBytes;
    public uint RecordSize;
    public uint InvalidInput; // 1 when the file opened but its content was rejected

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string ErrorMessage;
}
