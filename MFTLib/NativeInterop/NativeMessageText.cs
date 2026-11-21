using System.Runtime.InteropServices;

namespace MFTLib.NativeInterop;

/// <summary>Reads the text of a fixed native UTF-16 message buffer.</summary>
static class NativeMessageText
{
    /// <summary>
    ///     The characters before the first null unit. Native code null-terminates the text inside the buffer;
    ///     the units after it are not text.
    /// </summary>
    internal static string Read(ReadOnlySpan<ushort> units)
    {
        var length = units.IndexOf((ushort)0);
        return new string(MemoryMarshal.Cast<ushort, char>(length < 0 ? units : units[..length]));
    }
}
