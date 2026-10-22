using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.NativeInterop;

// Seam delegate for NativeDeviceIoControl: DeviceIoControl's out parameter rules out a
// plain Func<...>, so this mirrors Hooks.CreateFile's swappable-Func pattern with a named
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

static partial class Kernel32
{
    internal static readonly Win32Hooks Hooks = new();

    internal sealed class Win32Hooks
    {
        internal Func<string, uint, uint, IntPtr, uint, uint, IntPtr, SafeFileHandle>
            _createFile = NativeCreateFile;
        internal DeviceIoControl _deviceIoControl = NativeDeviceIoControl;

        internal void ResetToDefaults()
        {
            _createFile = NativeCreateFile;
            _deviceIoControl = NativeDeviceIoControl;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle NativeCreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NativeDeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "FlushViewOfFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool FlushViewOfFile(byte* baseAddress, nuint numberOfBytesToFlush);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformationW(
        string rootPathName, IntPtr volumeNameBuffer, uint volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        IntPtr fileSystemNameBuffer, uint fileSystemNameSize);
}

