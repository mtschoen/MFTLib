using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;

namespace MFTLib;

internal sealed partial class UsnJournalVolume
{
    /// <summary>
    ///     Yields batches of USN journal entries and their post-batch cursors as filesystem changes arrive.
    ///     Blocks on the kernel (zero CPU) until new entries appear.
    ///     Cancellation remains observable across read issuance and interrupts an idle kernel wait.
    ///     The pending read completes before its native buffers and cancellation event are released.
    /// </summary>
    internal async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchUsnJournal(
        UsnJournalCursor since,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_volumeHandle.IsClosed, this);
        var nextUsn = since.NextUsn;
        var journalIdentifier = since.JournalIdentifier;

        using var session = new UsnWatchSession(FileUtilities.Hooks.GetWatchVolumeHandle(_volumePath));
        await using var registration = cancellationToken.Register(session.Cancel);

        while (!cancellationToken.IsCancellationRequested)
        {
            ObjectDisposedException.ThrowIf(_volumeHandle.IsClosed, this);

            var currentUsn = nextUsn;
            var resultPtr = await session.ReadBatchAsync(currentUsn, journalIdentifier, cancellationToken)
                .ConfigureAwait(false);

            if (resultPtr == IntPtr.Zero)
            {
                throw new InvalidOperationException("WatchUsnJournalBatch returned null");
            }

            UsnJournalEntry[] entries;
            UsnJournalCursor cursor;
            try
            {
                var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    throw new InvalidOperationException(result.ErrorMessage);
                }

                entries = MarshalUsnEntries(result);
                nextUsn = result.NextUsn;
                cursor = new UsnJournalCursor(result.JournalIdentifier, result.NextUsn);
            }
            finally
            {
                MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
            }

            if (entries.Length == 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                // If ERROR_OPERATION_ABORTED (995) arrives without a requested cancellation,
                // the loop silently retries instead of surfacing an error; acceptable today
                // because CancelIoEx is only invoked from this token's registration.
                continue;
            }

            yield return (entries, cursor);
        }
    }
}
