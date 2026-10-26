using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.NativeInterop;

/// <summary>
///     File operations bound to an open handle rather than to a pathname: the file's identity, the final path the
///     file system reports for it, a reopen of the same file with other access, and an attributes-only open by path.
/// </summary>
static partial class HandleBoundFile
{
    const uint FileReadAttributes = 0x80;
    const uint Delete = 0x10000;
    const uint ShareAll = 0x7; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    const uint OpenExisting = 3;
    const uint FileFlagDeleteOnClose = 0x04000000;
    const uint FileFlagOpenReparsePoint = 0x00200000;
    const uint FileNameNormalized = 0x0;
    const uint VolumeNameDos = 0x0;

    const int FileIdInfoClass = 18; // FILE_INFO_BY_HANDLE_CLASS.FileIdInfo

    /// <summary>
    ///     The 64-bit volume serial number and 128-bit file id that name one file on one machine. The 64-bit index of
    ///     <c>GetFileInformationByHandle</c> is not unique on ReFS, so this reads <c>FILE_ID_INFO</c>, and it refuses
    ///     an all-zero or all-ones id, which a file system reports when it has no stable id.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe (ulong Volume, UInt128 File) IdentityOf(SafeFileHandle handle)
    {
        FileIdInformation information;
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, &information, (uint)sizeof(FileIdInformation)))
        {
            throw new IOException("Could not read the file identity.", Marshal.GetHRForLastWin32Error());
        }

        var file = new UInt128(information.FileIdHigh, information.FileIdLow);
        if (file == UInt128.Zero || file == UInt128.MaxValue)
        {
            throw new IOException("The file system reported no stable file identity.");
        }

        return (information.VolumeSerialNumber, file);
    }

    /// <summary>The name of the file system holding the open file, such as <c>NTFS</c>.</summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe string FileSystemNameOf(SafeFileHandle handle)
    {
        const int capacity = 64;
        var buffer = stackalloc char[capacity];
        if (!GetVolumeInformationByHandleW(handle, null, 0, null, null, null, buffer, capacity))
        {
            throw new IOException("Could not read the file system name.", Marshal.GetHRForLastWin32Error());
        }

        return new string(buffer);
    }

    /// <summary>The root of the volume <paramref name="path" /> is on, which is a folder for a mount point.</summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe string VolumePathOf(string path)
    {
        const int capacity = 1024;
        var buffer = stackalloc char[capacity];
        if (!GetVolumePathNameW(path, buffer, capacity))
        {
            throw new IOException($"Could not read the volume of '{path}'.", Marshal.GetHRForLastWin32Error());
        }

        return new string(buffer);
    }

    /// <summary>
    ///     The normalized final path of the open file, with every junction and symbolic link resolved, in its
    ///     <c>\\?\</c> form.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static unsafe string FinalPathOf(SafeFileHandle handle)
    {
        var capacity = 512u;
        while (true)
        {
            var buffer = new char[capacity];
            uint length;
            fixed (char* start = buffer)
            {
                length = GetFinalPathNameByHandleW(handle, start, capacity, FileNameNormalized | VolumeNameDos);
            }

            if (length == 0)
            {
                throw new IOException("Could not read the final path of the file.", Marshal.GetHRForLastWin32Error());
            }

            if (length < capacity)
            {
                return new string(buffer, 0, (int)length);
            }

            capacity = length + 1;
        }
    }

    /// <summary>An attributes-only handle on the same file, which takes no part in sharing checks.</summary>
    [SupportedOSPlatform("windows")]
    internal static SafeFileHandle ReopenForAttributes(SafeFileHandle handle) =>
        Checked(ReOpenFile(handle, FileReadAttributes, ShareAll, 0), "Could not reopen the file by its handle.");

    /// <summary>
    ///     Marks the file behind <paramref name="handle" /> for deletion through a reopen of that handle, so no
    ///     pathname is resolved; the file goes when its last handle closes. False when the reopen is refused.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static bool TryDeleteByHandle(SafeFileHandle handle)
    {
        using var deleter = ReOpenFile(handle, Delete, ShareAll, FileFlagDeleteOnClose);
        return !deleter.IsInvalid;
    }

    /// <summary>
    ///     An attributes-only handle on <paramref name="path" />, without following a reparse point in its last
    ///     component.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static SafeFileHandle OpenAttributesOnly(string path) =>
        Checked(CreateFileW(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint,
            IntPtr.Zero), $"Could not open '{path}'.");

    static SafeFileHandle Checked(SafeFileHandle handle, string message)
    {
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetHRForLastWin32Error();
        handle.Dispose();
        throw new IOException(message, error);
    }

    // Mirrors FILE_ID_INFO: a ULONGLONG volume serial number, then the 16-byte FILE_ID_128, read as two halves.
    [StructLayout(LayoutKind.Sequential)]
    struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(
        SafeFileHandle file, int informationClass, void* information, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeInformationByHandleW(
        SafeFileHandle file, char* volumeName, uint volumeNameSize, uint* volumeSerialNumber,
        uint* maximumComponentLength, uint* fileSystemFlags, char* fileSystemName, uint fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumePathNameW(string fileName, char* volumePath, uint volumePathSize);

    [LibraryImport("kernel32.dll", EntryPoint = "ReOpenFile", SetLastError = true)]
    private static partial SafeFileHandle ReOpenFile(
        SafeFileHandle originalFile, uint desiredAccess, uint shareMode, uint flagsAndAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandleW(
        SafeFileHandle file, char* filePath, uint filePathLength, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);
}
