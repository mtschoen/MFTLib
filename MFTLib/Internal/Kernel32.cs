using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MFTLib;

static class Kernel32
{
    internal static Func<string, uint, uint, IntPtr, uint, uint, IntPtr, SafeFileHandle> _createFile = NativeCreateFile;

    [DllImport("kernel32.dll", EntryPoint = "CreateFile", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle NativeCreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    internal static void ResetToDefaults()
    {
        _createFile = NativeCreateFile;
    }
}
