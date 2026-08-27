using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MFTLib;

// Seam delegate for NativeDeviceIoControl: DeviceIoControl's out parameter rules out a
// plain Func<...>, so this mirrors _createFile's swappable-Func pattern with a named
// delegate type instead.
delegate bool DeviceIoControl(
    SafeFileHandle device,
    uint ioControlCode,
    IntPtr inBuffer,
    uint inBufferSize,
    IntPtr outBuffer,
    uint outBufferSize,
    out uint bytesReturned,
    IntPtr overlapped);

static class Kernel32
{
    internal static Func<string, uint, uint, IntPtr, uint, uint, IntPtr, SafeFileHandle> _createFile = NativeCreateFile;
    internal static DeviceIoControl _deviceIoControl = NativeDeviceIoControl;

    [DllImport("kernel32.dll", EntryPoint = "CreateFile", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle NativeCreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    static extern bool NativeDeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    internal static void ResetToDefaults()
    {
        _createFile = NativeCreateFile;
        _deviceIoControl = NativeDeviceIoControl;
    }
}
