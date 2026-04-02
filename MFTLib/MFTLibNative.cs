using System.Runtime.InteropServices;

namespace MFTLib;

static class MFTLibNative
{
    private const string LibraryName = "MFTLibNative.dll";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern IntPtr ParseMFTRecords(SafeHandle volumeHandle, string? filter, uint matchFlags, uint bufferSizeRecords);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FreeMftResult(IntPtr result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern bool GenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern IntPtr ParseMFTFromFile(string filePath, string? filter, uint matchFlags, uint bufferSizeRecords);
}
