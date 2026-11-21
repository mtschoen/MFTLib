using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

/// <summary>
///     Mirrors the native <c>MftInlineRequest</c>: the caller fills the record number and expected sequence
///     number, the read fills the outcome and, for a returned content, its place in the caller's buffer.
///     Blittable, 32 bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct MftInlineRequest
{
    public ulong RecordNumber;
    public uint ExpectedSequenceNumber;
    public uint Outcome; // MftInlineOutcome: the InlineContentOutcome order
    public ulong ContentOffset;
    public uint ContentLength;
    public uint Reserved;
}
