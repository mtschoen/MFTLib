using System.Runtime.InteropServices;

namespace MFTLib;

static class MFTLibC
{
    private const string LibraryName = "MFTLibC.dll";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe void ExtractMFTRecords(SafeHandle volumeHandle, void* files, ulong filesCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void PrintVolumeInfo();
}