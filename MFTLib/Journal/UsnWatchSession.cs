using System.Runtime.InteropServices;
using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>
///     The native watch handle and the cancellation event one journal watch reads through.
///     Cancelling signals the event and the handle so an idle kernel wait returns; a pending
///     read must complete before the session is disposed.
/// </summary>
sealed class UsnWatchSession(SafeHandle watchHandle) : IDisposable
{
    readonly EventWaitHandle _cancellationEvent = new(false, EventResetMode.ManualReset);

    internal void Cancel()
    {
        _cancellationEvent.Set();
        MFTLibNative.NativeCancelUsnJournalWatch(watchHandle);
    }

    /// <summary>Blocks on a thread-pool thread until the journal has entries at or after the position.</summary>
    internal Task<IntPtr> ReadBatchAsync(long currentUsn, ulong journalIdentifier, CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
            () => MFTLibNative.NativeWatchUsnJournalBatchCancelable(
                watchHandle, currentUsn, journalIdentifier, _cancellationEvent.SafeWaitHandle),
            cancellationToken,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    void Dispose()
    {
        _cancellationEvent.Dispose();
        watchHandle.Dispose();
    }

    void IDisposable.Dispose() => Dispose();
}

