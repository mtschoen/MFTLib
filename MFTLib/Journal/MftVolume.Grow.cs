using System.ComponentModel;
using System.Runtime.InteropServices;
using MFTLib.Index;

namespace MFTLib;

public sealed partial class MftVolume
{
    /// <summary>
    ///     Grows this volume's USN change journal in place via
    ///     <c>FSCTL_CREATE_USN_JOURNAL</c>: the resize keeps every existing record. Grow
    ///     only, never shrink - a requested <paramref name="maximumSize" /> at or below the
    ///     current maximum is refused, because shrinking deletes history every journal
    ///     consumer on the volume (Windows Search, backup agents, replication) may still
    ///     need. Requires an active journal and an elevated handle. Returns the
    ///     post-change settings read back from the volume, which can round up past the
    ///     request to an allocation-delta multiple.
    /// </summary>
    public UsnJournalSettings GrowUsnJournal(long maximumSize, long allocationDelta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumSize, 0L);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(allocationDelta, 0L);

        var current = QueryUsnJournalSettings();
        if (maximumSize <= current.MaximumSize)
        {
            throw new InvalidOperationException(
                $"Refusing to resize the USN journal to {maximumSize} bytes: the current maximum " +
                $"is {current.MaximumSize} bytes, and only growth is permitted.");
        }

        // CREATE_USN_JOURNAL_DATA: two consecutive DWORDLONGs, maximum size then delta.
        var inBuffer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt64(inBuffer, 0, maximumSize);
            Marshal.WriteInt64(inBuffer, 8, allocationDelta);
            var succeeded = Kernel32._deviceIoControl(
                _volumeHandle, FsctlCreateUsnJournal, inBuffer, 16, IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuffer);
        }

        return QueryUsnJournalSettings();
    }

    const uint FsctlCreateUsnJournal = 0x000900E7;
}
