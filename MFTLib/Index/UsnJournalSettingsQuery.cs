using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Index;

/// <summary>
///     Reads one volume's USN change journal sizing via <c>FSCTL_QUERY_USN_JOURNAL</c>
///     against a backup-semantics handle on the volume's root directory, which needs no
///     elevation - the same handle recipe <see cref="WindowsFileById" /> documents, and the
///     same query <c>fsutil usn queryjournal</c> performs unelevated. Declared inside the
///     index (rather than reusing <c>MFTLib.Interop</c>) because the packed index is
///     substrate-neutral and may not reference the interop layer.
/// </summary>
[SupportedOSPlatform("windows")]
static class UsnJournalSettingsQuery
{
    const uint FileReadAttributes = 0x80;
    const uint ShareAll = 0x7; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    const uint OpenExisting = 3;
    const uint FileFlagBackupSemantics = 0x02000000;
    const uint FsctlQueryUsnJournal = 0x000900F4;

    /// <summary>
    ///     A test seam with no synchronization, which is safe only while the test host runs
    ///     one test at a time. Same shape as <c>IndexedDrive._volumeSerialReaderOverride</c>.
    /// </summary>
    internal static Func<char, UsnJournalSettings>? _queryOverride;

    public static UsnJournalSettings Query(char driveLetter)
    {
        if (_queryOverride is { } queryOverride)
        {
            return queryOverride(driveLetter);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "USN journal settings queries require Windows (FSCTL_QUERY_USN_JOURNAL).");
        }

        var rootDirectory = driveLetter + @":\";
        using var handle = CreateFileW(rootDirectory, FileReadAttributes, ShareAll, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException(
                $"Could not open '{rootDirectory}' to query its USN journal settings.",
                Marshal.GetHRForLastWin32Error());
        }

        var bufferSize = Marshal.SizeOf<UsnJournalDataV0>();
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var succeeded = NativeDeviceIoControl(
                handle, FsctlQueryUsnJournal, IntPtr.Zero, 0, buffer, (uint)bufferSize, out _, IntPtr.Zero);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var data = Marshal.PtrToStructure<UsnJournalDataV0>(buffer);
            return new UsnJournalSettings
            {
                MaximumSize = checked((long)data.MaximumSize),
                AllocationDelta = checked((long)data.AllocationDelta)
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static IDisposable OverrideQueryForTest(Func<char, UsnJournalSettings> query)
    {
        var previous = _queryOverride;
        _queryOverride = query;
        return new RestoreQuery(previous);
    }

    sealed class RestoreQuery(Func<char, UsnJournalSettings>? previous) : IDisposable
    {
        public void Dispose()
        {
            _queryOverride = previous;
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
    static extern bool NativeDeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    // Mirrors USN_JOURNAL_DATA_V0: seven 8-byte fields. Only the last two are read.
    [StructLayout(LayoutKind.Sequential)]
    struct UsnJournalDataV0
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
