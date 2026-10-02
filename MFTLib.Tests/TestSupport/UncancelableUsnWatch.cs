using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MFTLib.Tests.TestSupport;

internal static class UncancelableUsnWatch
{
    /// <summary>
    ///     Calls the cancelable native watch with a zero-valued event handle, which the native side treats as no
    ///     cancellation event.
    /// </summary>
    internal static IntPtr Read(SafeHandle volumeHandle, long startUsn, ulong journalId)
    {
        using var noCancellationEvent = new SafeWaitHandle(IntPtr.Zero, false);
        return MFTLibNative._watchUsnJournalBatchCancelable(volumeHandle, startUsn, journalId, noCancellationEvent);
    }
}
