using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

/// <summary>Mirrors the native <c>MftInlineKind</c>: what a request asks of its record.</summary>
enum MftInlineKind : uint
{
    /// <summary>The record's unnamed resident <c>$DATA</c>.</summary>
    Content = 0,

    /// <summary>
    ///     The name and parent a scan gives the file; returned content is the 8-byte parent file reference followed by
    ///     the name's UTF-16 units.
    /// </summary>
    FileName = 1
}

/// <summary>
///     Mirrors the native <c>MftInlineRequest</c>: the caller fills the record number, expected sequence number and
///     kind, the read fills the outcome and, for a returned content, its place in the caller's buffer.
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
    public MftInlineKind Kind; // every request of one call asks the same
}
