using System.ComponentModel;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;

namespace MFTLib.Index;

sealed unsafe partial class BlockFile
{
    internal delegate void SpinStep(ref SpinWait spinWait);

    const int ErrorLockViolation = 33;
    const int MaximumFlushWaits = 15;
    const int MaximumFlushRetriesPerWait = 20;

    /// <summary>
    ///     Flushes the whole view in consecutive ranges of at most <see cref="FlushRangeBytes" />,
    ///     in ascending offset order, so a caller learns of progress while a large block is
    ///     written out. After each range, <paramref name="rangeFlushed" /> receives the number of
    ///     bytes flushed so far. A failed native call throws and reports nothing for its range.
    /// </summary>
    public void Flush(Action<long>? rangeFlushed)
    {
        using var access = TakeAccess();
        FlushUnderHeldAccess(rangeFlushed);
    }

    /// <summary>
    ///     The range loop of <see cref="Flush" />. The caller must already hold a
    ///     <see cref="BlockAccessScope" /> on this block for the whole call: a caller that took its
    ///     access before disposal began must not ask for a second one, which disposal would refuse
    ///     although the view is still mapped for it.
    /// </summary>
    internal void FlushUnderHeldAccess(Action<long>? rangeFlushed)
    {
        var rangeBytes = Hooks._flushRangeBytes;
        for (long start = 0; start < Length; start += rangeBytes)
        {
            var end = Math.Min(start + rangeBytes, Length);
            FlushRange(start, end);
            rangeFlushed?.Invoke(end);
        }
    }

    int FlushViewRangeNatively(long start, long end)
    {
        var pointer = _base + _view.PointerOffset;
        return Kernel32.FlushViewOfFile(pointer + start, (nuint)(end - start)) ? 0 : Marshal.GetLastWin32Error();
    }

    void FlushRange(long start, long end)
    {
        if (OperatingSystem.IsWindows())
        {
            FlushViewWithRetry(start, end);
            return;
        }

        SynchronizeRange(start, end, ClassifyPlatform(OperatingSystem.IsLinux(), OperatingSystem.IsMacOS()));
    }

    /// <summary>
    ///     Flushes one range through <c>msync</c> with <paramref name="platform" />'s native
    ///     <c>MS_SYNC | MS_INVALIDATE</c> flags. msync needs a page-aligned address, so the range
    ///     start moves down to its page boundary. A failed call throws with its errno.
    /// </summary>
    internal void SynchronizeRange(long start, long end, OSPlatform platform)
    {
        // Match .NET 10's Unix view flush; libc needs native flags, not the runtime's PAL values.
        // https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.MemoryMappedFiles/src/System/IO/MemoryMappedFiles/MemoryMappedView.Unix.cs
        var flag = Libc.SelectSynchronousFlag(platform);
        var pageBytes = Environment.SystemPageSize;
        var alignedStart = start - (start % pageBytes);
        var error = Hooks._synchronizeViewRange(alignedStart, end, flag);
        if (error != 0)
        {
            throw new IOException($"msync failed with errno {error}.");
        }
    }

    int SynchronizeViewRangeNatively(long start, long end, int flag)
    {
        var pointer = _base + _view.PointerOffset;
        return Libc.msync(pointer + start, (nuint)(end - start), flag) == 0 ? 0 : Marshal.GetLastWin32Error();
    }

    /// <summary>
    ///     Maps the operating system checks to the platform <see cref="Libc.SelectSynchronousFlag" />
    ///     understands; a platform that is neither Linux nor macOS stays unrecognized so the
    ///     selector refuses it before any native call.
    /// </summary>
    internal static OSPlatform ClassifyPlatform(bool isLinux, bool isMacOs)
    {
        if (isLinux)
        {
            return OSPlatform.Linux;
        }

        return isMacOs ? OSPlatform.OSX : OSPlatform.Create("UNRECOGNIZED");
    }

    /// <summary>
    ///     Flushes one range, retrying only <c>ERROR_LOCK_VIOLATION</c>, an intermittent NTFS
    ///     transaction log failure: the runtime's own view flush pauses for 1, 2, 4 ... ms across
    ///     <see cref="MaximumFlushWaits" /> waits of <see cref="MaximumFlushRetriesPerWait" />
    ///     attempts each, then gives up. Any other error throws at once.
    /// </summary>
    internal void FlushViewWithRetry(long start, long end)
    {
        var error = Hooks._flushViewRange(start, end);
        if (error == 0)
        {
            return;
        }

        var spinWait = default(SpinWait);
        for (var wait = 0; error == ErrorLockViolation && wait < MaximumFlushWaits; wait++)
        {
            Hooks._pauseMilliseconds(1 << wait);
            for (var retry = 0; retry < MaximumFlushRetriesPerWait; retry++)
            {
                error = Hooks._flushViewRange(start, end);
                if (error == 0)
                {
                    return;
                }

                if (error != ErrorLockViolation)
                {
                    break;
                }

                Hooks._spinOnce(ref spinWait);
            }
        }

        throw new Win32Exception(error);
    }
}
