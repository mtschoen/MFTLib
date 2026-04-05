using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace MFTLib;

static class Kernel32
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFile", SetLastError = true, CharSet = CharSet.Auto)]
    static extern SafeFileHandle NativeCreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    internal static Func<string, uint, uint, IntPtr, uint, uint, IntPtr, SafeFileHandle> CreateFile = NativeCreateFile;

    internal static void ResetToDefaults()
    {
        CreateFile = NativeCreateFile;
    }
}
