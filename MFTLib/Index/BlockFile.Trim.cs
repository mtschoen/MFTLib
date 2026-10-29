using System.Runtime.InteropServices;
using MFTLib.NativeInterop;

namespace MFTLib.Index;

sealed unsafe partial class BlockFile
{
    /// <summary>The Windows error that <c>VirtualUnlock</c> reports, with a FALSE return, once it has trimmed an unlocked range.</summary>
    const int ErrorNotLocked = 158;

    /// <summary>
    ///     Drops the resident pages of rows <paramref name="startRow" /> up to <paramref name="endRowExclusive" /> from the
    ///     process working set, so a long scan does not leave the whole row table resident. Only whole pages
    ///     inside the row range are trimmed (the range is aligned inward): neighbouring partitions scan
    ///     concurrently and share their boundary pages, and a trim never reaches a page outside the range
    ///     asked for, at the cost of at most two untrimmed pages per call. The mapping stays valid and
    ///     every page faults back from the page cache on its next read, so the call never changes what a
    ///     reader sees. Pages a writer dirtied are written back by the operating system, not discarded:
    ///     <c>VirtualUnlock</c> moves them to the modified list and <c>madvise(MADV_DONTNEED)</c> on a
    ///     shared file mapping transfers the dirty bit to the page cache. Taking an access scope makes disposal wait
    ///     for the call, and a disposed or disposing block is skipped.
    /// </summary>
    /// <returns>
    ///     True when the operating system accepted the trim or the range holds no whole page; false when the block
    ///     is being disposed, the range is invalid, or the native call failed. The trim is advisory, so a false
    ///     result never affects a scan.
    /// </returns>
    internal bool TryReleaseRowPages(uint startRow, uint endRowExclusive)
    {
        if (startRow > endRowExclusive || endRowExclusive > SlotCapacity)
        {
            return false;
        }

        var pageBytes = (long)Environment.SystemPageSize;
        var start = AlignUp(BlockLayout.RowRegionOffset + (long)startRow * BlockLayout.RowBytes, pageBytes);
        var end = AlignDown(BlockLayout.RowRegionOffset + (long)endRowExclusive * BlockLayout.RowBytes, pageBytes);
        if (end <= start)
        {
            return true;
        }

        if (!TryTakeAccess())
        {
            return false;
        }

        try
        {
            return Hooks._trimViewRange(start, end) == 0;
        }
        finally
        {
            ReturnAccess();
        }
    }

    static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    static long AlignDown(long value, long alignment) => value / alignment * alignment;

    int TrimViewRangeNatively(long start, long end)
    {
        var pointer = _base + _view.PointerOffset;
        var length = (nuint)(end - start);
        if (OperatingSystem.IsWindows())
        {
            // The documented failure of a trim: the range was never locked.
            return Kernel32.VirtualUnlock(pointer + start, length) ? 0 : ExpectedTrimError(Marshal.GetLastWin32Error());
        }

        return Libc.MemoryAdvise(pointer + start, length, Libc.AdviseDontNeed) == 0 ? 0 : Marshal.GetLastWin32Error();
    }

    static int ExpectedTrimError(int error) => error == ErrorNotLocked ? 0 : error;
}
