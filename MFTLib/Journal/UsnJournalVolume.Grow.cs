using System.ComponentModel;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>
///     USN change journal resizing operations over a borrowed volume handle.
/// </summary>
sealed partial class UsnJournalVolume
{
    internal const long MaximumRecommendedGrowthBytes = 8L * 1024 * 1024 * 1024;
    internal const long RecommendedGrowthAlignmentBytes = 1024L * 1024;

    /// <summary>
    ///     Grows this volume's USN change journal in place via
    ///     <c>FSCTL_CREATE_USN_JOURNAL</c>: the resize keeps every existing record. Grow
    ///     only, never shrink: when the effective target maximum is at or below the current
    ///     maximum, returns <see cref="UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget" />
    ///     without issuing <c>FSCTL_CREATE_USN_JOURNAL</c>, because shrinking deletes history
    ///     every journal consumer on the volume (Windows Search, backup agents, replication)
    ///     may still need. Without <paramref name="allowAboveMaximumRecommendedSize" />, a
    ///     requested <paramref name="maximumSize" /> above <see cref="QueryMaximumRecommendedSize" />
    ///     (the smaller of 8 GiB and 5 percent of the volume's total size queried at request
    ///     time, rounded down to a multiple of 1 MiB) is clamped to that recommended cap and
    ///     reported as <see cref="UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended" />; with
    ///     <paramref name="allowAboveMaximumRecommendedSize" />, the full requested size is applied
    ///     and reported as <see cref="UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride" />.
    ///     After <c>FSCTL_CREATE_USN_JOURNAL</c> succeeds, the volume's journal settings are read
    ///     back: if the read-back <see cref="UsnJournalSettings.MaximumSize" /> is not greater than
    ///     the pre-call maximum, returns <see cref="UsnJournalGrowthOutcome.Unchanged" /> with the
    ///     settings the volume kept. Requires an active journal and an elevated handle.
    /// </summary>
    /// <param name="maximumSize">Requested maximum journal size in bytes. Must be positive.</param>
    /// <param name="allocationDelta">Journal allocation granularity in bytes. Must be positive.</param>
    /// <param name="allowAboveMaximumRecommendedSize">
    ///     When true, bypasses the recommended cap and applies <paramref name="maximumSize" />
    ///     as requested; when false, clamps a request above the cap to <see cref="QueryMaximumRecommendedSize" />.
    /// </param>
    /// <returns>The classified growth outcome, the requested and maximum recommended sizes, and the journal settings Windows reports afterward.</returns>
    /// <exception cref="ObjectDisposedException">This volume has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Either size is not positive.</exception>
    /// <exception cref="InvalidOperationException">The volume does not report usable capacity geometry.</exception>
    /// <exception cref="Win32Exception">
    ///     Windows rejects the disk space query or the journal creation control request.
    /// </exception>
    internal UsnJournalGrowthResult GrowUsnJournal(
        long maximumSize,
        long allocationDelta,
        bool allowAboveMaximumRecommendedSize = false)
    {
        ObjectDisposedException.ThrowIf(_volumeHandle.IsClosed, this);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumSize, 0L);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(allocationDelta, 0L);

        var maximumRecommended = QueryMaximumRecommendedSize(_volumePath);
        var exceedsMaximumRecommended = maximumSize > maximumRecommended;
        var effectiveMaximumSize = exceedsMaximumRecommended && !allowAboveMaximumRecommendedSize
            ? maximumRecommended
            : maximumSize;

        var current = QueryUsnJournalSettings();
        if (current.MaximumSize >= effectiveMaximumSize)
        {
            return new UsnJournalGrowthResult
            {
                Outcome = UsnJournalGrowthOutcome.AlreadyAtOrAboveTarget,
                RequestedMaximumSize = maximumSize,
                MaximumRecommendedSize = maximumRecommended,
                JournalSettings = current
            };
        }

        // CREATE_USN_JOURNAL_DATA: two consecutive DWORDLONGs, maximum size then delta.
        var inBuffer = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt64(inBuffer, 0, effectiveMaximumSize);
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

        var updated = QueryUsnJournalSettings();
        var outcome = updated.MaximumSize <= current.MaximumSize
            ? UsnJournalGrowthOutcome.Unchanged
            : !exceedsMaximumRecommended
                ? UsnJournalGrowthOutcome.GrewAsRequested
                : allowAboveMaximumRecommendedSize
                    ? UsnJournalGrowthOutcome.GrewAboveMaximumRecommendedByOverride
                    : UsnJournalGrowthOutcome.GrewClampedToMaximumRecommended;

        return new UsnJournalGrowthResult
        {
            Outcome = outcome,
            RequestedMaximumSize = maximumSize,
            MaximumRecommendedSize = maximumRecommended,
            JournalSettings = updated
        };
    }

    internal static long QueryMaximumRecommendedSize(string volumePath)
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
        var unalignedCapBytes = Math.Min(MaximumRecommendedGrowthBytes, fivePercentBytes);
        var alignedCapBytes = unalignedCapBytes - (unalignedCapBytes % RecommendedGrowthAlignmentBytes);
        if (alignedCapBytes == 0)
        {
            throw new InvalidOperationException(
                $"Volume '{rootPath}' total size ({totalBytes} bytes) is too small to compute a positive USN journal cap.");
        }

        return (long)alignedCapBytes;
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
