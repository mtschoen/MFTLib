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

// Seam delegate for NativeGetFileInformationByHandle: the out parameter rules out a
// plain Func<...>, mirroring the DeviceIoControl delegate above.
delegate bool GetFileInformationByHandle(
    SafeFileHandle file,
    out ByHandleFileInformation fileInformation);

// Mirrors the Win32 BY_HANDLE_FILE_INFORMATION layout. Every member is 4-aligned, so a
// uint pair stands in for each FILETIME; only FileIndexHigh/FileIndexLow are read -
// together they are the file's 64-bit NTFS file reference number.
[StructLayout(LayoutKind.Sequential)]
struct ByHandleFileInformation
{
    public uint FileAttributes;
    public uint CreationTimeLow;
    public uint CreationTimeHigh;
    public uint LastAccessTimeLow;
    public uint LastAccessTimeHigh;
    public uint LastWriteTimeLow;
    public uint LastWriteTimeHigh;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}

static class Kernel32
{
    internal static Func<string, uint, uint, IntPtr, uint, uint, IntPtr, SafeFileHandle> _createFile = NativeCreateFile;
    internal static DeviceIoControl _deviceIoControl = NativeDeviceIoControl;
    internal static GetFileInformationByHandle _getFileInformationByHandle = NativeGetFileInformationByHandle;

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

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    static extern bool NativeGetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    internal static void ResetToDefaults()
    {
        _createFile = NativeCreateFile;
        _deviceIoControl = NativeDeviceIoControl;
        _getFileInformationByHandle = NativeGetFileInformationByHandle;
    }
}
