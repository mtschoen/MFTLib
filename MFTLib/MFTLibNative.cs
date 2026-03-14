using System.Runtime.InteropServices;

namespace MFTLib;

static class MFTLibNative
{
    private const string LibraryName = "MFTLibNative.dll";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern bool ParseMFT(SafeHandle volumeHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void PrintVolumeInfo(SafeHandle volumeHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern IntPtr ParseMFTRecords(SafeHandle volumeHandle, string? filter, uint matchFlags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void FreeMftResult(IntPtr result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern bool GenerateSyntheticMFT(string filePath, ulong recordCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern IntPtr ParseMFTFromFile(string filePath, string? filter, uint matchFlags);
}
