using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

/// <summary>
///     The native <c>MftParseSink</c> a streaming parse delivers to: the slice and extension functions and the
///     context both receive. The caller keeps it in place for the whole parse call.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
struct MftParseSink
{
    public IntPtr Slice;
    public IntPtr Extensions;
    public IntPtr Context;
}

/// <summary>
///     The native <c>MftExtensionDataSize</c>, 18 packed bytes: the data size an allocated extension record carries
///     for its base record, which a streaming parse delivers once every slice is delivered.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
readonly struct MftExtensionDataSize(ulong baseRecordNumber, long size, ushort baseSequenceNumber)
{
    public readonly ulong BaseRecordNumber = baseRecordNumber;
    public readonly long Size = size;
    public readonly ushort BaseSequenceNumber = baseSequenceNumber;
}
