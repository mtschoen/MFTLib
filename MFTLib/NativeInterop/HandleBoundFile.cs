using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.NativeInterop;

/// <summary>
///     File operations bound to an open handle rather than to a pathname: the file's identity, the final path the
///     file system reports for it, a reopen of the same file with other access, and an attributes-only open by path.
/// </summary>
[SupportedOSPlatform("windows")]
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

    /// <summary>The volume serial number and 64-bit file index that name one file on one volume.</summary>
    internal static (uint Volume, ulong Index) IdentityOf(SafeFileHandle handle)
    {
        if (!Kernel32.GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException("Could not read the file identity.", Marshal.GetHRForLastWin32Error());
        }

        return (information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    /// <summary>
    ///     The normalized final path of the open file, with every junction and symbolic link resolved, in its
    ///     <c>\\?\</c> form.
    /// </summary>
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
    internal static SafeFileHandle ReopenForAttributes(SafeFileHandle handle) =>
        Checked(ReOpenFile(handle, FileReadAttributes, ShareAll, 0), "Could not reopen the file by its handle.");

    /// <summary>
    ///     Marks the file behind <paramref name="handle" /> for deletion through a reopen of that handle, so no
    ///     pathname is resolved; the file goes when its last handle closes. False when the reopen is refused.
    /// </summary>
    internal static bool TryDeleteByHandle(SafeFileHandle handle)
    {
        using var deleter = ReOpenFile(handle, Delete, ShareAll, FileFlagDeleteOnClose);
        return !deleter.IsInvalid;
    }

    /// <summary>
    ///     An attributes-only handle on <paramref name="path" />, without following a reparse point in its last
    ///     component.
    /// </summary>
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
