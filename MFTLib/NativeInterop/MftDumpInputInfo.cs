using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

/// <summary>
///     What the native open of a dump found. <see cref="ErrorMessage" /> is empty exactly when the
///     open returned an input. Blittable, so the open is a generated <c>LibraryImport</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
unsafe struct MftDumpInputInfo
{
    const int ErrorMessageLength = 256; // MFT_ERROR_MESSAGE_UNITS

    public ulong LengthBytes;
    public uint RecordSize;
    public uint InvalidInput; // 1 when the file opened but its content was rejected
    public fixed ushort ErrorMessageUnits[ErrorMessageLength];

    // Native code null-terminates the text inside the buffer; the units after it are not text.
    public string ErrorMessage
    {
        get
        {
            var units = MemoryMarshal.CreateReadOnlySpan(ref ErrorMessageUnits[0], ErrorMessageLength);
            var length = units.IndexOf((ushort)0);
            return new string(MemoryMarshal.Cast<ushort, char>(length < 0 ? units : units[..length]));
        }
    }
}
