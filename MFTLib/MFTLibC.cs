using System.Runtime.InteropServices;

namespace MFTLib;

public static class MFTLibC
{
    [DllImport("MFTLibC.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    public static extern void PrintFileSize();
}