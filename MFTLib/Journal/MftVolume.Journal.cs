using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Interop;

namespace MFTLib;

internal sealed partial class MftVolume
{
    // Native UsnJournalEntry layout (pack 1):
    //   recordNumber(8) + parentRecordNumber(8) + usn(8) + timestamp(8) +
    //   reason(4) + fileAttributes(4) + fileNameLength(2) + fileName(260*2=520) +
    //   sequenceNumber(2) = 564 bytes
    internal const int NativeUsnEntrySize = 564;

    /// <summary>
    ///     Query the USN journal to get the current cursor position.
    ///     Capture this before a full MFT scan, then read from it after the scan, to
    ///     include changes that occurred while the scan was running.
    /// </summary>
    public UsnJournalCursor QueryUsnJournalCursor()
    {
        var info = QueryJournalInfo();
        return new UsnJournalCursor(info.JournalId, info.NextUsn);
    }

    /// <summary>
    ///     Query the USN journal's sizing (maximum size and allocation delta), the two
    ///     settings <see cref="GrowUsnJournal" /> changes. Returns the current maximum size
    ///     and allocation delta. Consumers own retention targets and sizing policy.
    /// </summary>
    public UsnJournalSettings QueryUsnJournalSettings()
    {
        var info = QueryJournalInfo();
        return new UsnJournalSettings
        {
            MaximumSize = checked((long)info.MaximumSize),
            AllocationDelta = checked((long)info.AllocationDelta)
        };
    }

    UsnJournalInfoNative QueryJournalInfo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var infoPtr = MFTLibNative._queryUsnJournal(_volumeHandle);
        if (infoPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("QueryUsnJournal returned null");
        }

        try
        {
            var info = Marshal.PtrToStructure<UsnJournalInfoNative>(infoPtr);
            if (!string.IsNullOrEmpty(info.ErrorMessage))
            {
                throw new InvalidOperationException(info.ErrorMessage);
            }

            return info;
        }
        finally
        {
            MFTLibNative._freeUsnJournalInfo(infoPtr);
        }
    }

    /// <summary>
    ///     Read USN journal entries since the given cursor.
    ///     Returns entries and an updated cursor for the next call.
    ///     Throws InvalidOperationException if the journal was recreated or entries
    ///     were overwritten - caller should fall back to a full MFT rescan.
    /// </summary>
    public (UsnJournalEntry[] Entries, UsnJournalCursor UpdatedCursor) ReadUsnJournal(UsnJournalCursor since)
    {
        return ReadUsnJournalBounded(since, 0);
    }

    /// <summary>
    ///     Reads at most <paramref name="maximumBufferReads" /> journal buffers (64 KB each) since the
    ///     given cursor; 0 reads to the journal tip. The updated cursor resumes where the read stopped.
    /// </summary>
    internal (UsnJournalEntry[] Entries, UsnJournalCursor UpdatedCursor) ReadUsnJournalBounded(
        UsnJournalCursor since, int maximumBufferReads)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBufferReads);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resultPtr = MFTLibNative._readUsnJournal(_volumeHandle, since.NextUsn, since.JournalId,
            (uint)maximumBufferReads);
        if (resultPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("ReadUsnJournal returned null");
        }

        try
        {
            var result = Marshal.PtrToStructure<UsnJournalResultNative>(resultPtr);
            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                throw new InvalidOperationException(result.ErrorMessage);
            }

            var entries = MarshalUsnEntries(result);
            var updatedCursor = new UsnJournalCursor(result.JournalId, result.NextUsn);
            return (entries, updatedCursor);
        }
        finally
        {
            MFTLibNative._freeUsnJournalResult(resultPtr);
        }
    }

    static unsafe UsnJournalEntry[] MarshalUsnEntries(UsnJournalResultNative result)
    {
        var count = (int)result.EntryCount;
        if (count == 0)
        {
            return [];
        }

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
            var sequenceNumber = *(ushort*)(ptr + 562);

            entries[i] = new UsnJournalEntry(new NativeUsnJournalEntryData
            {
                RecordNumber = recordNumber,
                ParentRecordNumber = parentRecordNumber,
                SequenceNumber = sequenceNumber,
                Usn = usn,
                FileTimeTimestamp = timestamp,
                Reason = reason,
                FileAttributes = fileAttributes,
                FileName = fileName
            });
        }

        return entries;
    }

    /// <summary>
    ///     Yields batches of USN journal entries and their post-batch cursors as filesystem changes arrive.
    ///     Blocks on the kernel (zero CPU) until new entries appear.
    ///     Cancellation remains observable across read issuance and interrupts an idle kernel wait.
    ///     The pending read completes before its native buffers and cancellation event are released.
    /// </summary>
    public async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> WatchUsnJournal(
        UsnJournalCursor since,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var nextUsn = since.NextUsn;
        var journalId = since.JournalId;

        using var session = new UsnWatchSession(FileUtilities._getWatchVolumeHandle(_volumePath));
        await using var registration = cancellationToken.Register(session.Cancel);

        while (!cancellationToken.IsCancellationRequested)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var currentUsn = nextUsn;
            var resultPtr = await session.ReadBatchAsync(currentUsn, journalId, cancellationToken)
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
                cursor = new UsnJournalCursor(result.JournalId, result.NextUsn);
            }
            finally
            {
                MFTLibNative._freeUsnJournalResult(resultPtr);
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
