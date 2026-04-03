using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace MFTLib;

class Kernel32
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    internal static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(SafeHandle hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);


    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        IntPtr lpNewFilePointer,
        uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer,
        uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned,
        IntPtr lpOverlapped);
}