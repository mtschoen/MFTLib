using System.Runtime.InteropServices;

namespace MFTLib;

static class MFTLibC
{
    [DllImport("MFTLibC.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe void ExtractMFTRecords(SafeHandle volumeHandle, void* files, ulong filesCount);
}