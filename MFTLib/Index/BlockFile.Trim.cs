using System.Runtime.InteropServices;
using MFTLib.NativeInterop;

namespace MFTLib.Index;

sealed unsafe partial class BlockFile
{
    /// <summary>The Windows error that <c>VirtualUnlock</c> reports, with a FALSE return, once it has trimmed an unlocked range.</summary>
    const int ErrorNotLocked = 158;

    /// <summary>
    ///     Drops the resident pages of the whole mapped view (header, rows, sequence numbers, name entries,
    ///     buckets and name pool) from the process working set. Only whole pages are trimmed. The mapping stays
    ///     valid and every page faults back from the page cache on its next read, so the call never changes what a
    ///     reader sees and is safe beside concurrent readers and writers. Pages a writer dirtied are written back
    ///     by the operating system, not discarded: <c>VirtualUnlock</c> moves them to the modified list and
    ///     <c>madvise(MADV_DONTNEED)</c> on a shared file mapping transfers the dirty bit to the page cache.
    ///     Taking an access scope makes disposal wait for the call, and a disposed or disposing block is skipped.
    /// </summary>
    /// <returns>
    ///     True when the operating system accepted the trim or the view holds no whole page; false when the block
    ///     is being disposed or the native call failed. The trim is advisory.
    /// </returns>
    internal bool TryReleaseResidentPages()
    {
        var pageBytes = (long)Environment.SystemPageSize;
        var start = 0L;
        var end = AlignDown(Length, pageBytes);
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
