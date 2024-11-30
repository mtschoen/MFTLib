using System.Runtime.InteropServices;

namespace MFTLib;

static class MFTLibNative
{
    private const string LibraryName = "MFTLibNative.dll";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ParseMFT(SafeHandle volumeHandle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void PrintVolumeInfo(SafeHandle volumeHandle);
}