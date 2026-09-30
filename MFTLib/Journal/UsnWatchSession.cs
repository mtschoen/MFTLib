using System.Runtime.InteropServices;

namespace MFTLib;

/// <summary>
///     The native watch handle and the cancellation event one journal watch reads through.
///     Cancelling signals the event and the handle so an idle kernel wait returns; a pending
///     read must complete before the session is disposed.
/// </summary>
sealed class UsnWatchSession(SafeHandle watchHandle) : IDisposable
{
    readonly EventWaitHandle _cancellationEvent = new(false, EventResetMode.ManualReset);

    public void Cancel()
    {
        _cancellationEvent.Set();
        MFTLibNative._cancelUsnJournalWatch(watchHandle);
    }

    /// <summary>Blocks on a thread-pool thread until the journal has entries at or after the position.</summary>
    public Task<IntPtr> ReadBatchAsync(long currentUsn, ulong journalId, CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
            () => MFTLibNative._watchUsnJournalBatchCancelable(
                watchHandle, currentUsn, journalId, _cancellationEvent.SafeWaitHandle),
            cancellationToken,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        _cancellationEvent.Dispose();
        watchHandle.Dispose();
    }
}
