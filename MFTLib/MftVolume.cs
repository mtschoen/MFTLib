using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MFTLib.Interop;

namespace MFTLib;

public sealed class MftVolume : IDisposable
{
    private readonly SafeFileHandle _volumeHandle;
    private readonly string _driveLetter;
    private bool _disposed;

    private MftVolume(SafeFileHandle volumeHandle, string driveLetter)
    {
        _volumeHandle = volumeHandle;
        _driveLetter = driveLetter;
    }

    public static MftVolume Open(string driveLetter)
    {
        var letter = driveLetter.TrimEnd(':');
        var volumePath = MFTUtilities.GetFileNameForDriveLetter(letter);
        var handle = FileUtilities.GetVolumeHandle(volumePath);
        return new MftVolume(handle, letter);
    }

    public MftRecord[] ReadAllRecords() => ReadAllRecords(out _);

    public MftRecord[] ReadAllRecords(out MftParseTimings timings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IntPtr resultPtr = MFTLibNative.ParseMFTRecords(_volumeHandle);
        if (resultPtr == IntPtr.Zero)
            throw new InvalidOperationException("ParseMFTRecords returned null");

        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPtr);

            if (!string.IsNullOrEmpty(result.ErrorMessage) && result.UsedRecords == 0)
                throw new InvalidOperationException($"MFT parse failed: {result.ErrorMessage}");

            int entrySize = Marshal.SizeOf<MftFileEntry>();
            var records = new MftRecord[result.UsedRecords];
            nint currentPtr = result.Entries;

            var marshalSw = Stopwatch.StartNew();
            for (ulong i = 0; i < result.UsedRecords; i++)
            {
                var entry = Marshal.PtrToStructure<MftFileEntry>(currentPtr);
                records[i] = new MftRecord(entry.RecordNumber, entry.ParentRecordNumber, entry.Flags, entry.FileName);
                currentPtr += entrySize;
            }
            marshalSw.Stop();

            timings = new MftParseTimings(
                result.IoTimeMs, result.FixupTimeMs, result.ParseTimeMs,
                result.TotalTimeMs, marshalSw.Elapsed.TotalMilliseconds);

            return records;
        }
        finally
        {
            MFTLibNative.FreeMftResult(resultPtr);
        }
    }

    public IEnumerable<MftRecord> FindByName(string name, bool exactMatch = true)
    {
        var records = ReadAllRecords();
        var comparison = StringComparison.OrdinalIgnoreCase;

        foreach (var record in records)
        {
            if (exactMatch
                ? string.Equals(record.FileName, name, comparison)
                : record.FileName.Contains(name, comparison))
            {
                yield return record;
            }
        }
    }

    public IEnumerable<string> FindDirectories(string name)
    {
        var records = ReadAllRecords();
        var lookup = new Dictionary<ulong, MftRecord>();
        foreach (var r in records)
            lookup[r.RecordNumber] = r;

        foreach (var record in records)
        {
            if (record.IsDirectory && string.Equals(record.FileName, name, StringComparison.OrdinalIgnoreCase))
            {
                yield return ResolvePath(record.RecordNumber, lookup);
            }
        }
    }

    public string ResolvePath(ulong recordNumber)
    {
        var records = ReadAllRecords();
        var lookup = new Dictionary<ulong, MftRecord>();
        foreach (var r in records)
            lookup[r.RecordNumber] = r;
        return ResolvePath(recordNumber, lookup);
    }

    private string ResolvePath(ulong recordNumber, Dictionary<ulong, MftRecord> lookup)
    {
        var parts = new List<string>();
        var current = recordNumber;
        var visited = new HashSet<ulong>();

        while (current != 5 && lookup.TryGetValue(current, out var record) && visited.Add(current))
        {
            parts.Add(record.FileName);
            current = record.ParentRecordNumber;
        }

        parts.Reverse();
        return $"{_driveLetter}:\\{string.Join('\\', parts)}";
    }

    public static void GenerateSyntheticMFT(string filePath, ulong recordCount)
    {
        if (!MFTLibNative.GenerateSyntheticMFT(filePath, recordCount))
            throw new InvalidOperationException("Failed to generate synthetic MFT file");
    }

    public static MftRecord[] ParseMFTFromFile(string filePath, out MftParseTimings timings)
    {
        IntPtr resultPtr = MFTLibNative.ParseMFTFromFile(filePath);
        if (resultPtr == IntPtr.Zero)
            throw new InvalidOperationException("ParseMFTFromFile returned null");

        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPtr);

            if (!string.IsNullOrEmpty(result.ErrorMessage) && result.UsedRecords == 0)
                throw new InvalidOperationException($"MFT parse failed: {result.ErrorMessage}");

            int entrySize = Marshal.SizeOf<MftFileEntry>();
            var records = new MftRecord[result.UsedRecords];
            nint currentPtr = result.Entries;

            var marshalSw = Stopwatch.StartNew();
            for (ulong i = 0; i < result.UsedRecords; i++)
            {
                var entry = Marshal.PtrToStructure<MftFileEntry>(currentPtr);
                records[i] = new MftRecord(entry.RecordNumber, entry.ParentRecordNumber, entry.Flags, entry.FileName);
                currentPtr += entrySize;
            }
            marshalSw.Stop();

            timings = new MftParseTimings(
                result.IoTimeMs, result.FixupTimeMs, result.ParseTimeMs,
                result.TotalTimeMs, marshalSw.Elapsed.TotalMilliseconds);

            return records;
        }
        finally
        {
            MFTLibNative.FreeMftResult(resultPtr);
        }
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
