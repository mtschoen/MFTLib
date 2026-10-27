using System.ComponentModel;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>
///     USN change journal resizing operations over a borrowed volume handle.
/// </summary>
sealed partial class UsnJournalVolume
{
    internal const long MaximumAllowedGrowthBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>
    ///     Grows this volume's USN change journal in place via
    ///     <c>FSCTL_CREATE_USN_JOURNAL</c>: the resize keeps every existing record. Grow
    ///     only, never shrink - a requested <paramref name="maximumSize" /> at or below the
    ///     current maximum is refused, because shrinking deletes history every journal
    ///     consumer on the volume (Windows Search, backup agents, replication) may still
    ///     need. The requested <paramref name="maximumSize" /> is also capped at the smaller
    ///     of 8 GiB and 5% of the volume's total size queried at request time. Requires an
    ///     active journal and an elevated handle. Returns the post-change settings read back
    ///     from the volume, which can round up past the request to an allocation-delta multiple.
    /// </summary>
    /// <param name="maximumSize">
    ///     Requested maximum journal size in bytes. It must exceed the current maximum and
    ///     must not exceed the smaller of 8 GiB and 5% of the volume's total size.
    /// </param>
    /// <param name="allocationDelta">Journal allocation granularity in bytes.</param>
    /// <returns>The journal settings Windows reports after growth.</returns>
    /// <exception cref="ObjectDisposedException">This volume has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     Either size is not positive, or <paramref name="maximumSize" /> exceeds the smaller
    ///     of 8 GiB and 5% of the volume's total size.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="maximumSize" /> does not grow the journal, or the volume does not
    ///     report usable capacity geometry.
    /// </exception>
    /// <exception cref="Win32Exception">
    ///     Windows rejects the disk space query or the journal creation control request.
    /// </exception>
    internal UsnJournalSettings GrowUsnJournal(long maximumSize, long allocationDelta)
    {
        ObjectDisposedException.ThrowIf(_volumeHandle.IsClosed, this);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumSize, 0L);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(allocationDelta, 0L);

        var (maximumAllowed, totalBytes, rootPath) = QueryMaximumAllowedJournalSize(_volumePath);
        if (maximumSize > maximumAllowed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSize),
                maximumSize,
                $"Requested USN journal maximum size of {maximumSize} bytes exceeds the allowed cap of {maximumAllowed} bytes " +
                $"(the smaller of 8 GiB and 5% of volume '{rootPath}' size {totalBytes} bytes).");
        }

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
            var succeeded = Kernel32.Hooks._deviceIoControl(
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

    static (long MaximumAllowed, ulong TotalBytes, string RootPath) QueryMaximumAllowedJournalSize(string volumePath)
    {
        var rootPath = VolumeRootPath(volumePath);
        var hresult = Kernel32.Hooks._getDiskSpaceInformation(rootPath, out var diskSpace);
        if (hresult < 0)
        {
            throw new Win32Exception(hresult);
        }

        if (diskSpace.ActualTotalAllocationUnits == 0 ||
            diskSpace.SectorsPerAllocationUnit == 0 ||
            diskSpace.BytesPerSector == 0)
        {
            throw new InvalidOperationException(
                $"Volume '{rootPath}' reported unusable disk geometry " +
                $"({diskSpace.ActualTotalAllocationUnits} allocation units, " +
                $"{diskSpace.SectorsPerAllocationUnit} sectors per unit, " +
                $"{diskSpace.BytesPerSector} bytes per sector).");
        }

        ulong totalBytes;
        try
        {
            totalBytes = checked(diskSpace.ActualTotalAllocationUnits *
                                 diskSpace.SectorsPerAllocationUnit *
                                 diskSpace.BytesPerSector);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                $"Volume '{rootPath}' disk size overflowed 64-bit unsigned byte arithmetic.", exception);
        }

        var fivePercentBytes = totalBytes / 20UL;
        if (fivePercentBytes == 0)
        {
            throw new InvalidOperationException(
                $"Volume '{rootPath}' total size ({totalBytes} bytes) is too small to compute a positive USN journal cap.");
        }

        var maximumAllowed = (long)Math.Min(MaximumAllowedGrowthBytes, fivePercentBytes);
        return (maximumAllowed, totalBytes, rootPath);
    }

    static string VolumeRootPath(string volumePath)
    {
        if (volumePath.Length == 6 &&
            volumePath.StartsWith(@"\\.\", StringComparison.Ordinal) &&
            char.IsAsciiLetterUpper(volumePath[4]) &&
            volumePath[5] == ':')
        {
            return $"{volumePath[4]}:\\";
        }

        throw new InvalidOperationException(
            $"Cannot determine an explicit volume root from volume path '{volumePath}'.");
    }

    const uint FsctlCreateUsnJournal = 0x000900E7;
}
