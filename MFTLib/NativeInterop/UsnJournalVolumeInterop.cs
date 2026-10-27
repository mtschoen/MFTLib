using System.ComponentModel;
using System.Runtime.InteropServices;
using MFTLib.Index;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.NativeInterop;

/// <summary>
///     The unelevated volume-root handle recipe shared by the index's USN journal queries,
///     and the journal query IOCTL they issue against it. The handle is the one
///     <see cref="WindowsFileById" /> documents - backup semantics, attributes-only access -
///     which is also what <c>fsutil usn queryjournal</c> uses unelevated.
/// </summary>
static class UsnJournalVolumeInterop
{
    const uint FileReadAttributes = 0x80;
    const uint ShareAll = 0x7; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    const uint OpenExisting = 3;
    const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 61, METHOD_BUFFERED, FILE_ANY_ACCESS).</summary>
    internal const uint FsctlQueryUsnJournal = 0x000900F4;

    /// <summary>
    ///     Opens a volume's root directory for journal queries. <paramref name="purpose" />
    ///     names the query in the failure message, since the caller knows which one it is.
    /// </summary>
    internal static SafeFileHandle OpenVolumeRoot(VolumeKey volume, string purpose)
    {
        var rootDirectory = volume + @":\";
        var handle = Kernel32.Hooks._createFile(rootDirectory, FileReadAttributes, ShareAll, IntPtr.Zero,
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
    internal static UsnJournalDataV0 QueryJournalData(SafeFileHandle handle)
    {
        var bufferSize = Marshal.SizeOf<UsnJournalDataV0>();
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var succeeded = Kernel32.Hooks._deviceIoControl(
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

    /// <summary>
    ///     Mirrors <c>USN_JOURNAL_DATA_V0</c>: seven 8-byte fields. The whole layout is
    ///     declared because the kernel fills it; <c>LowestValidUsn</c> and <c>MaxUsn</c> are
    ///     there to place the fields that follow them, not because anything reads them.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct UsnJournalDataV0
    {
        public ulong UsnJournalIdentifier;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;

        /// <summary>The sizing the journal reports, the one projection every settings query returns.</summary>
        public readonly UsnJournalSettings ToSettings()
        {
            return new UsnJournalSettings
            {
                MaximumSize = checked((long)MaximumSize),
                AllocationDelta = checked((long)AllocationDelta)
            };
        }
    }
}
