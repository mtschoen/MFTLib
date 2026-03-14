using System.Diagnostics;
using System.Runtime.CompilerServices;
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

            var marshalSw = Stopwatch.StartNew();
            var records = ReadEntriesUnsafe(result.Entries, result.UsedRecords);
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

            var marshalSw = Stopwatch.StartNew();
            var records = ReadEntriesUnsafe(result.Entries, result.UsedRecords);
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

    // Native MftFileEntry layout (pack=1): u64 recordNumber, u64 parentRecordNumber,
    // u16 flags, u16 fileNameLength, wchar_t[260] fileName = 540 bytes total.
    private const int NativeEntrySize = 8 + 8 + 2 + 2 + 260 * 2; // 540

    private static unsafe MftRecord[] ReadEntriesUnsafe(IntPtr entries, ulong count)
    {
        var records = new MftRecord[count];
        byte* ptr = (byte*)entries;

        for (ulong i = 0; i < count; i++)
        {
            ulong recordNumber = Unsafe.ReadUnaligned<ulong>(ptr);
            ulong parentRecordNumber = Unsafe.ReadUnaligned<ulong>(ptr + 8);
            ushort flags = Unsafe.ReadUnaligned<ushort>(ptr + 16);
            ushort nameLength = Unsafe.ReadUnaligned<ushort>(ptr + 18);

            var fileName = new string((char*)(ptr + 20), 0, nameLength);

            records[i] = new MftRecord(recordNumber, parentRecordNumber, flags, fileName);
            ptr += NativeEntrySize;
        }

        return records;
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
