using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Index;

/// <summary>
///     Opens an NTFS file by its file reference (record number plus sequence number) instead of
///     by path, through <c>OpenFileById</c>. The reference handle needs only a directory already
///     open on the target volume with backup semantics, which needs no elevation - not
///     specifically the volume root, despite the Win32 parameter's <c>hVolumeHint</c> name.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsFileById
{
    const uint FileReadAttributes = 0x80;
    const uint GenericRead = 0x80000000;
    const uint GenericWrite = 0x40000000;
    const uint ShareAll = 0x7; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    const uint OpenExisting = 3;
    const uint FileFlagBackupSemantics = 0x02000000;

    public static FileStream Open(string anyPathOnVolume, uint recordNumber, ushort sequenceNumber,
        FileAccess access)
    {
        using var volumeHandle = CreateFileW(anyPathOnVolume, FileReadAttributes, ShareAll, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (volumeHandle.IsInvalid)
        {
            throw new IOException(
                $"Could not open '{anyPathOnVolume}' to resolve record {recordNumber}.",
                Marshal.GetHRForLastWin32Error());
        }

        var descriptor = new FileIdDescriptor
        {
            DwSize = (uint)Marshal.SizeOf<FileIdDescriptor>(),
            Type = 0,
            FileId = ((long)sequenceNumber << 48) | recordNumber
        };

        var fileHandle = OpenFileById(volumeHandle, ref descriptor, ToDesiredAccess(access), ShareAll,
            IntPtr.Zero, 0);
        if (fileHandle.IsInvalid)
        {
            // Read before the handle is released, because disposing it can overwrite the last
            // error, and released before the throw, which is what the volume handle's using above
            // does for the sibling failure: an invalid handle still owns one until something
            // frees it, and leaving that to the finalizer is a release this method can make now.
            var error = Marshal.GetHRForLastWin32Error();
            fileHandle.Dispose();
            throw new IOException(
                $"Could not open record {recordNumber} (sequence {sequenceNumber}) on '{anyPathOnVolume}'.",
                error);
        }

        return new FileStream(fileHandle, access);
    }

    static uint ToDesiredAccess(FileAccess access)
    {
        return access switch
        {
            FileAccess.Read => GenericRead,
            FileAccess.Write => GenericWrite,
            FileAccess.ReadWrite => GenericRead | GenericWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access), access, "Unrecognized FileAccess value.")
        };
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "OpenFileById", SetLastError = true)]
    static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint,
        ref FileIdDescriptor fileId,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint flagsAndAttributes);

    // Mirrors the Win32 FILE_ID_DESCRIPTOR layout: DWORD dwSize, FILE_ID_TYPE Type, then a
    // 16-byte union whose largest members are GUID/FILE_ID_128. FileId (LARGE_INTEGER, 8
    // bytes) is the only union member this codebase uses; the trailing field is unused but
    // still declared, public, so the struct's total size (24 bytes) matches what the kernel
    // expects for dwSize and so the union's unread bytes are not mistaken for dead code.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct FileIdDescriptor
    {
        public uint DwSize;
        public int Type;
        public long FileId;
        public long UnusedUnionTail;
    }
}
