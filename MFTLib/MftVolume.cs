using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MFTLib;

public sealed class MftVolume : IDisposable
{
    readonly SafeFileHandle _volumeHandle;
    readonly string _driveLetter;
    readonly uint _bufferSizeRecords;
    bool _disposed;

    MftVolume(SafeFileHandle volumeHandle, string driveLetter, uint bufferSizeRecords)
    {
        _volumeHandle = volumeHandle;
        _driveLetter = driveLetter;
        _bufferSizeRecords = bufferSizeRecords;
    }

    public static MftVolume Open(string volumePath, uint bufferSizeRecords = 262144)
    {
        var normalizedPath = MFTUtilities.GetVolumePath(volumePath);
        var handle = FileUtilities.GetVolumeHandle(normalizedPath);

        var driveLetter = ExtractDriveLetter(normalizedPath);

        return new MftVolume(handle, driveLetter, bufferSizeRecords);
    }

    public MftRecord[] ReadAllRecords() => ReadAllRecords(resolvePaths: false, out _);

    public MftRecord[] ReadAllRecords(bool resolvePaths) => ReadAllRecords(resolvePaths, out _);

    public MftRecord[] ReadAllRecords(out MftParseTimings timings)
    {
        return ReadAllRecords(resolvePaths: false, out timings);
    }

    public MftRecord[] ReadAllRecords(bool resolvePaths, out MftParseTimings timings)
    {
        using var result = StreamRecords(null, resolvePaths ? MatchFlags.ResolvePaths : MatchFlags.None);
        return MaterializeWithTimings(result, out timings);
    }

    public MftRecord[] FindByName(string name, MatchFlags matchFlags = MatchFlags.ExactMatch)
        => FindByName(name, matchFlags, out _);

    public MftRecord[] FindByName(string name, MatchFlags matchFlags, out MftParseTimings timings)
    {
        using var result = StreamRecords(name, matchFlags);
        return MaterializeWithTimings(result, out timings);
    }

    public MftResult StreamRecords(string? filter = null, MatchFlags matchFlags = MatchFlags.None)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resultPtr = MFTLibNative.ParseMFTRecords(_volumeHandle, filter, matchFlags, _bufferSizeRecords);

        if (resultPtr == IntPtr.Zero)
            throw new InvalidOperationException("ParseMFTRecords returned null");

        return new MftResult(resultPtr, _driveLetter, 0);
    }

    public IEnumerable<string> FindDirectories(string name)
    {
        return FindRecords(name, isDirectory: true);
    }

    public IEnumerable<string> FindFiles(string name)
    {
        return FindRecords(name, isDirectory: false);
    }

    public IEnumerable<string> FindRecords(string name, bool? isDirectory = null)
    {
        using var result = StreamRecords(name, MatchFlags.ExactMatch | MatchFlags.ResolvePaths);

        foreach (var record in result)
        {
            if (isDirectory.HasValue && record.IsDirectory != isDirectory.Value)
                continue;

            yield return record.FullPath ?? record.FileName;
        }
    }

    public static void GenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords = 262144)
    {
        if (!MFTLibNative.GenerateSyntheticMFT(filePath, recordCount, bufferSizeRecords))
            throw new InvalidOperationException("Failed to generate synthetic MFT file");
    }

    public static MftRecord[] ParseMFTFromFile(string filePath, out MftParseTimings timings)
        => ParseMFTFromFile(filePath, null, MatchFlags.None, out timings);

    public static MftRecord[] ParseMFTFromFile(string filePath, string? filter, MatchFlags matchFlags, out MftParseTimings timings, uint bufferSizeRecords = 262144)
    {
        var resultPtr = MFTLibNative.ParseMFTFromFile(filePath, filter, matchFlags, bufferSizeRecords);

        if (resultPtr == IntPtr.Zero)
            throw new InvalidOperationException("ParseMFTFromFile returned null");

        using var result = new MftResult(resultPtr, string.Empty, 0);
        return MaterializeWithTimings(result, out timings);
    }

    static MftRecord[] MaterializeWithTimings(MftResult result, out MftParseTimings timings)
    {
        var sw = Stopwatch.StartNew();
        var records = result.ToArray();
        sw.Stop();
        timings = result.Timings.WithMarshalMs(sw.Elapsed.TotalMilliseconds);
        return records;
    }

    /// <summary>
    /// Query the USN journal to get the current cursor position.
    /// Use this after a full MFT scan to establish a baseline for incremental updates.
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

            var resultPtr = await Task.Run(
                () => MFTLibNative.WatchUsnJournalBatch(_volumeHandle, nextUsn, journalId),
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

    internal static string ExtractDriveLetter(string normalizedPath)
    {
        if (normalizedPath.Length != 6) return string.Empty;
        if (!normalizedPath.StartsWith(@"\\.\", StringComparison.Ordinal)) return string.Empty;
        if (normalizedPath[5] != ':') return string.Empty;
        return normalizedPath[4].ToString();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _volumeHandle.Dispose();
            _disposed = true;
        }
    }
}
