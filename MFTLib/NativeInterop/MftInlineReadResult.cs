using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

/// <summary>
///     What one native inline-content read did: how far it got, the buffer it filled, whether it was cancelled
///     or could not run, and its I/O counters. Blittable.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
unsafe struct MftInlineReadResult
{
    const int ErrorMessageLength = 256; // MFT_ERROR_MESSAGE_UNITS

    public ulong NextRequest; // the first request not yet answered; the request count when the read finished
    public ulong BytesUsed;
    public ulong ReadCalls;
    public ulong BytesRead;
    public uint Cancelled;
    public uint Failed; // 1 when the call could not run; ErrorMessage says why
    public fixed ushort ErrorMessageUnits[ErrorMessageLength];

    public string ErrorMessage =>
        NativeMessageText.Read(MemoryMarshal.CreateReadOnlySpan(ref ErrorMessageUnits[0], ErrorMessageLength));
}
