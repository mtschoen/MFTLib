using System.ComponentModel;
using System.Runtime.InteropServices;
using MFTLib.NativeInterop;
using Microsoft.Win32.SafeHandles;

namespace MFTLib;

/// <summary>
///     USN change journal read, cursor, and query operations over a borrowed volume handle.
/// </summary>
internal sealed partial class UsnJournalVolume
{
    // Native UsnJournalEntry layout (pack 1):
    //   recordNumber(8) + parentRecordNumber(8) + usn(8) + timestamp(8) +
    //   reason(4) + fileAttributes(4) + fileNameLength(2) + fileName(260*2=520) +
    //   sequenceNumber(2) = 564 bytes
    internal const int NativeUsnEntrySize = 564;

    readonly SafeFileHandle _volumeHandle;
    readonly string _volumePath;

    internal UsnJournalVolume(SafeFileHandle volumeHandle, string volumePath)
    {
        _volumeHandle = volumeHandle;
        _volumePath = volumePath;
    }

    /// <summary>
    ///     Query the USN journal to get the current cursor position.
    ///     Capture this before a full MFT scan, then read from it after the scan, to
    ///     include changes that occurred while the scan was running.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This volume has been disposed.</exception>
    /// <exception cref="Win32Exception">Windows rejects the journal query, for example because the journal is not active.</exception>
    internal UsnJournalCursor QueryUsnJournalCursor()
    {
        var data = QueryJournalData();
        return new UsnJournalCursor(data.UsnJournalIdentifier, data.NextUsn);
    }

    /// <summary>
    ///     Query the USN journal's sizing (maximum size and allocation delta), the two
    ///     settings <see cref="GrowUsnJournal" /> changes. Returns the current maximum size
    ///     and allocation delta. The library reports the size that would have retained a lost
    ///     checkpoint; the consumer owns the decision to grow.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This volume has been disposed.</exception>
    /// <exception cref="Win32Exception">Windows rejects the journal query, for example because the journal is not active.</exception>
    internal UsnJournalSettings QueryUsnJournalSettings()
    {
        return QueryJournalData().ToSettings();
    }

    // The one journal query: the same FSCTL_QUERY_USN_JOURNAL call FileIndex.QueryUsnJournalSettings
    // and the checkpoint check issue, against this volume's handle.
    UsnJournalVolumeInterop.UsnJournalDataV0 QueryJournalData()
    {
        ObjectDisposedException.ThrowIf(_volumeHandle.IsClosed, this);
        return UsnJournalVolumeInterop.QueryJournalData(_volumeHandle);
    }

    /// <summary>
    ///     Reads at most <paramref name="maximumBufferReads" /> journal buffers (64 KB each) since the
    ///     given cursor; 0 reads to the journal tip. The updated cursor resumes where the read stopped.
    ///     Throws InvalidOperationException if the journal was recreated or entries
    ///     were overwritten - caller should fall back to a full MFT rescan.
    /// </summary>
    internal (UsnJournalEntry[] Entries, UsnJournalCursor UpdatedCursor) ReadUsnJournalBounded(
        UsnJournalCursor since, int maximumBufferReads)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBufferReads);
        ObjectDisposedException.ThrowIf(_volumeHandle.IsClosed, this);
        var resultPtr = MFTLibNative.NativeReadUsnJournal(_volumeHandle, since.NextUsn, since.JournalIdentifier,
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
            var updatedCursor = new UsnJournalCursor(result.JournalIdentifier, result.NextUsn);
            return (entries, updatedCursor);
        }
        finally
        {
            MFTLibNative.NativeFreeUsnJournalResult(resultPtr);
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
}
