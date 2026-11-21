using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

/// <summary>
///     Mirrors the native <c>MftInlineReadCall</c>: the requests a read answers from <see cref="StartIndex" /> on,
///     the buffer it places returned content in, and the control block that cancels it. Every address stays valid
///     for the call. Blittable.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct MftInlineReadCall
{
    public IntPtr Requests; // MftInlineRequest*
    public ulong RequestCount;
    public ulong StartIndex;
    public IntPtr Buffer; // byte*, at least MFTLibNative.MinimumInlineContentBufferBytes
    public ulong BufferLength;
    public IntPtr Control; // MftParseControl*, or zero
}
