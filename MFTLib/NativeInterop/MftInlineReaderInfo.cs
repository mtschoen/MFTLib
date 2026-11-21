using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

/// <summary>
///     What the native open of an inline-content reader found. <see cref="ErrorMessage" /> is empty exactly
///     when the open returned a reader. Blittable, so the open is a generated <c>LibraryImport</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
unsafe struct MftInlineReaderInfo
{
    const int ErrorMessageLength = 256; // MFT_ERROR_MESSAGE_UNITS

    public ulong TotalRecords;
    public uint RecordSize;
    public fixed ushort ErrorMessageUnits[ErrorMessageLength];

    public string ErrorMessage =>
        NativeMessageText.Read(MemoryMarshal.CreateReadOnlySpan(ref ErrorMessageUnits[0], ErrorMessageLength));
}
