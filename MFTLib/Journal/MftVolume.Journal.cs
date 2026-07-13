using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MFTLib;

public sealed partial class MftVolume
{
    /// <summary>
    /// Query the USN journal to get the current cursor position.
    /// Capture this before a full MFT scan, then read from it after the scan, to
    /// include changes that occurred while the scan was running.
    /// </summary>
    public UsnJournalCursor QueryUsnJournal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var infoPtr = MFTLibNative.QueryUsnJournal(_volumeHandle);
        if (infoPtr == IntPtr.Zero)
            throw new InvalidOperationException("QueryUsnJournal returned null");

        try
        {
            var info = Marshal.PtrToStructure<Interop.UsnJournalInfoNative>(infoPtr);
            if (!string.IsNullOrEmpty(info.ErrorMessage))
                throw new InvalidOperationException(info.ErrorMessage);

            return new UsnJournalCursor(info.JournalId, info.NextUsn);
        }
        finally
        {
            MFTLibNative.FreeUsnJournalInfo(infoPtr);
        }
    }

    /// <summary>
    /// Read USN journal entries since the given cursor.
    /// Returns entries and an updated cursor for the next call.
    /// Throws InvalidOperationException if the journal was recreated or entries
    /// were overwritten — caller should fall back to a full MFT rescan.
    /// </summary>
    public (UsnJournalEntry[] Entries, UsnJournalCursor UpdatedCursor) ReadUsnJournal(UsnJournalCursor since)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resultPtr = MFTLibNative.ReadUsnJournal(_volumeHandle, since.NextUsn, since.JournalId);
        if (resultPtr == IntPtr.Zero)
            throw new InvalidOperationException("ReadUsnJournal returned null");

        try
        {
            var result = Marshal.PtrToStructure<Interop.UsnJournalResultNative>(resultPtr);
            if (!string.IsNullOrEmpty(result.ErrorMessage))
                throw new InvalidOperationException(result.ErrorMessage);

            var entries = MarshalUsnEntries(result);
            var updatedCursor = new UsnJournalCursor(result.JournalId, result.NextUsn);
            return (entries, updatedCursor);
        }
        finally
        {
            MFTLibNative.FreeUsnJournalResult(resultPtr);
        }
    }

    // Native UsnJournalEntry layout (pack 1):
    //   recordNumber(8) + parentRecordNumber(8) + usn(8) + timestamp(8) +
    //   reason(4) + fileAttributes(4) + fileNameLength(2) + fileName(260*2=520)
    //   = 562 bytes
    internal const int NativeUsnEntrySize = 562;

    static unsafe UsnJournalEntry[] MarshalUsnEntries(Interop.UsnJournalResultNative result)
    {
        var count = (int)result.EntryCount;
        if (count == 0) return [];
        var entries = new UsnJournalEntry[count];
        var basePtr = (byte*)result.Entries;

        for (var i = 0; i < count; i++)
        {
            var ptr = basePtr + i * NativeUsnEntrySize;
            var recordNumber = *(ulong*)ptr;
            var parentRecordNumber = *(ulong*)(ptr + 8);
            var usn = *(long*)(ptr + 16);
            var timestamp = *(long*)(ptr + 24);
            var reason = *(uint*)(ptr + 32);
            var fileAttributes = *(uint*)(ptr + 36);
            var fileNameLength = *(ushort*)(ptr + 40);
            var fileName = new string((char*)(ptr + 42), 0, fileNameLength);

            entries[i] = new UsnJournalEntry(recordNumber, parentRecordNumber,
                usn, timestamp, reason, fileAttributes, fileName);
        }

        return entries;
    }

    /// <summary>
    /// Yields batches of USN journal entries as filesystem changes arrive.
    /// Blocks on the kernel (zero CPU) until new entries appear.
    /// Cancel the token to stop watching — unblocks the kernel wait via CancelIoEx.
    /// </summary>
    public async IAsyncEnumerable<UsnJournalEntry[]> WatchUsnJournal(
        UsnJournalCursor since,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var nextUsn = since.NextUsn;
        var journalId = since.JournalId;

        using var registration = cancellationToken.Register(() =>
            MFTLibNative.CancelUsnJournalWatch(_volumeHandle));

        while (!cancellationToken.IsCancellationRequested)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var currentUsn = nextUsn;
            var resultPtr = await Task.Run(
                () => MFTLibNative.WatchUsnJournalBatch(_volumeHandle, currentUsn, journalId),
                cancellationToken).ConfigureAwait(false);

            if (resultPtr == IntPtr.Zero)
                throw new InvalidOperationException("WatchUsnJournalBatch returned null");

            UsnJournalEntry[] entries;
            try
            {
                var result = Marshal.PtrToStructure<Interop.UsnJournalResultNative>(resultPtr);
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    throw new InvalidOperationException(result.ErrorMessage);

                entries = MarshalUsnEntries(result);
                nextUsn = result.NextUsn;
            }
            finally
            {
                MFTLibNative.FreeUsnJournalResult(resultPtr);
            }

            if (entries.Length == 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;
                continue;
            }

            yield return entries;
        }
    }

    /// <summary>
    /// Like <see cref="WatchUsnJournal"/> but yields the post-batch cursor
    /// alongside each batch, so callers can persist progress without a
    /// separate <see cref="QueryUsnJournal"/> IOCTL.
    /// </summary>
    public async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchUsnJournalWithCursor(
        UsnJournalCursor since,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var nextUsn = since.NextUsn;
        var journalId = since.JournalId;

        using var registration = cancellationToken.Register(() =>
            MFTLibNative.CancelUsnJournalWatch(_volumeHandle));

        while (!cancellationToken.IsCancellationRequested)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var currentUsn = nextUsn;
            var resultPtr = await Task.Run(
                () => MFTLibNative.WatchUsnJournalBatch(_volumeHandle, currentUsn, journalId),
                cancellationToken).ConfigureAwait(false);

            if (resultPtr == IntPtr.Zero)
                throw new InvalidOperationException("WatchUsnJournalBatch returned null");

            UsnJournalEntry[] entries;
            UsnJournalCursor cursor;
            try
            {
                var result = Marshal.PtrToStructure<Interop.UsnJournalResultNative>(resultPtr);
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    throw new InvalidOperationException(result.ErrorMessage);

                entries = MarshalUsnEntries(result);
                nextUsn = result.NextUsn;
                cursor = new UsnJournalCursor(result.JournalId, result.NextUsn);
            }
            finally
            {
                MFTLibNative.FreeUsnJournalResult(resultPtr);
            }

            if (entries.Length == 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;
                continue;
            }

            yield return (entries, cursor);
        }
    }

}
