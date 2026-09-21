using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Index;

/// <summary>
///     Seam delegate for the <c>DeviceIoControl</c> import below: the out parameter rules
///     out a plain <see cref="Func{T, TResult}" />, so this mirrors the named-delegate
///     pattern <c>Kernel32</c> uses. Declared here rather than reused from <c>Kernel32</c>
///     because the index may not reference the flat namespace.
/// </summary>
delegate bool VolumeIoControl(
    SafeFileHandle device,
    uint ioControlCode,
    IntPtr inBuffer,
    uint inBufferSize,
    IntPtr outBuffer,
    uint outBufferSize,
    out uint bytesReturned,
    IntPtr overlapped);

/// <summary>
///     The unelevated volume-root handle recipe shared by the index's USN journal queries,
///     and the two IOCTLs they issue against it. The handle is the one
///     <see cref="WindowsFileById" /> documents - backup semantics, attributes-only access -
///     which is also what <c>fsutil usn queryjournal</c> uses unelevated. Declared inside
///     the index (rather than reusing <c>MFTLib.Interop</c>) because the packed index is
///     substrate-neutral and may not reference the interop layer.
/// </summary>
[SupportedOSPlatform("windows")]
static class UsnJournalVolumeInterop
{
    const uint FileReadAttributes = 0x80;
    const uint ShareAll = 0x7; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    const uint OpenExisting = 3;
    const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 61, METHOD_BUFFERED, FILE_ANY_ACCESS).</summary>
    internal const uint FsctlQueryUsnJournal = 0x000900F4;

    /// <summary>
    ///     <c>FSCTL_READ_UNPRIVILEGED_USN_JOURNAL</c>:
    ///     CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 234, METHOD_NEITHER, FILE_ANY_ACCESS). The
    ///     privileged <c>FSCTL_READ_USN_JOURNAL</c> fails with ERROR_ACCESS_DENIED on this
    ///     unelevated handle; this variant succeeds and blanks the file names of records the
    ///     caller cannot otherwise see, which costs nothing here because only each record's
    ///     timestamp is read.
    /// </summary>
    internal const uint FsctlReadUnprivilegedUsnJournal = 0x000903AB;

    /// <summary>
    ///     A test seam with no synchronization, which is safe only while the test host runs
    ///     one test at a time. Same shape as <c>Kernel32._deviceIoControl</c>.
    /// </summary>
    internal static VolumeIoControl _ioControl = DeviceIoControl;

    internal static void ResetToDefaults()
    {
        _ioControl = DeviceIoControl;
    }

    /// <summary>
    ///     Opens a drive's root directory for journal queries. <paramref name="purpose" />
    ///     names the query in the failure message, since the caller knows which one it is.
    /// </summary>
    public static SafeFileHandle OpenVolumeRoot(char driveLetter, string purpose)
    {
        var rootDirectory = driveLetter + @":\";
        var handle = CreateFileW(rootDirectory, FileReadAttributes, ShareAll, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException(
                $"Could not open '{rootDirectory}' to {purpose}.",
                Marshal.GetHRForLastWin32Error());
        }

        return handle;
    }

    /// <summary>Issues <c>FSCTL_QUERY_USN_JOURNAL</c> against an open volume-root handle.</summary>
    public static UsnJournalDataV0 QueryJournalData(SafeFileHandle handle)
    {
        var bufferSize = Marshal.SizeOf<UsnJournalDataV0>();
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var succeeded = _ioControl(
                handle, FsctlQueryUsnJournal, IntPtr.Zero, 0, buffer, (uint)bufferSize, out _, IntPtr.Zero);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Marshal.PtrToStructure<UsnJournalDataV0>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    /// <summary>
    ///     Mirrors <c>USN_JOURNAL_DATA_V0</c>: seven 8-byte fields. The whole layout is
    ///     declared because the kernel fills it; <c>LowestValidUsn</c> and <c>MaxUsn</c> are
    ///     there to place the fields that follow them, not because anything reads them.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct UsnJournalDataV0
    {
        public ulong UsnJournalId;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }
}
