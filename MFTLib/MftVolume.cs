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

    public uint BufferSizeRecords { get; set; } = 262144;

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

    public MftRecord[] ReadAllRecords() => ReadAllRecords(resolvePaths: false, out _);

    public MftRecord[] ReadAllRecords(bool resolvePaths) => ReadAllRecords(resolvePaths, out _);

    public MftRecord[] ReadAllRecords(out MftParseTimings timings)
    {
        return ReadAllRecords(resolvePaths: false, out timings);
    }

    public MftRecord[] ReadAllRecords(bool resolvePaths, out MftParseTimings timings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ParseNative(null, resolvePaths ? 4u : 0u, out timings);
    }

    public MftRecord[] FindByName(string name, bool exactMatch = true)
        => FindByName(name, exactMatch, out _);

    public MftRecord[] FindByName(string name, bool exactMatch, out MftParseTimings timings)
        => FindByName(name, exactMatch, resolvePaths: false, out timings);

    public MftRecord[] FindByName(string name, bool exactMatch, bool resolvePaths, out MftParseTimings timings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        uint matchFlags = (exactMatch ? 1u : 2u) | (resolvePaths ? 4u : 0u);
        return ParseNative(name, matchFlags, out timings);
    }

    private MftRecord[] ParseNative(string? filter, uint matchFlags, out MftParseTimings timings)
    {
        IntPtr resultPtr = MFTLibNative.ParseMFTRecords(_volumeHandle, filter, matchFlags, BufferSizeRecords);
        if (resultPtr == IntPtr.Zero)
            throw new InvalidOperationException("ParseMFTRecords returned null");

        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPtr);

            if (!string.IsNullOrEmpty(result.ErrorMessage) && result.UsedRecords == 0)
                throw new InvalidOperationException($"MFT parse failed: {result.ErrorMessage}");

            var marshalSw = Stopwatch.StartNew();
            MftRecord[] records;
            if (result.PathEntries != IntPtr.Zero)
                records = ReadPathEntriesUnsafe(result.PathEntries, result.UsedRecords, _driveLetter);
            else
                records = ReadEntriesUnsafe(result.Entries, result.UsedRecords);
            marshalSw.Stop();

            timings = new MftParseTimings(
                result.TotalRecords, result.IoTimeMs, result.FixupTimeMs, result.ParseTimeMs,
                result.TotalTimeMs, marshalSw.Elapsed.TotalMilliseconds);

            return records;
        }
        finally
        {
            MFTLibNative.FreeMftResult(resultPtr);
        }
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
        var records = FindByName(name, exactMatch: true, resolvePaths: true, out _);

        foreach (var record in records)
        {
            if (isDirectory.HasValue && record.IsDirectory != isDirectory.Value)
                continue;

            yield return record.FullPath ?? record.FileName;
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
        return MftPathUtilities.ResolvePath(recordNumber, lookup, _driveLetter);
    }

    public static void GenerateSyntheticMFT(string filePath, ulong recordCount, uint bufferSizeRecords = 262144)
    {
        if (!MFTLibNative.GenerateSyntheticMFT(filePath, recordCount, bufferSizeRecords))
            throw new InvalidOperationException("Failed to generate synthetic MFT file");
    }

    public static MftRecord[] ParseMFTFromFile(string filePath, out MftParseTimings timings)
        => ParseMFTFromFile(filePath, null, 0, out timings);

    public static MftRecord[] ParseMFTFromFile(string filePath, string? filter, uint matchFlags, out MftParseTimings timings, uint bufferSizeRecords = 262144)
    {
        IntPtr resultPtr = MFTLibNative.ParseMFTFromFile(filePath, filter, matchFlags, bufferSizeRecords);
        if (resultPtr == IntPtr.Zero)
            throw new InvalidOperationException("ParseMFTFromFile returned null");

        try
        {
            var result = Marshal.PtrToStructure<MftParseResult>(resultPtr);

            if (!string.IsNullOrEmpty(result.ErrorMessage) && result.UsedRecords == 0)
                throw new InvalidOperationException($"MFT parse failed: {result.ErrorMessage}");

            var marshalSw = Stopwatch.StartNew();
            MftRecord[] records;
            if (result.PathEntries != IntPtr.Zero)
                records = ReadPathEntriesUnsafe(result.PathEntries, result.UsedRecords, "");
            else
                records = ReadEntriesUnsafe(result.Entries, result.UsedRecords);
            marshalSw.Stop();

            timings = new MftParseTimings(
                result.TotalRecords, result.IoTimeMs, result.FixupTimeMs, result.ParseTimeMs,
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

    // Native MftPathEntry layout (pack=1): u64 recordNumber, u64 parentRecordNumber,
    // u16 flags, u16 pathLength, wchar_t[1024] path = 2068 bytes total.
    private const int NativePathEntrySize = 8 + 8 + 2 + 2 + 1024 * 2; // 2068

    private const int ParallelThreshold = 500_000;

    private static unsafe MftRecord[] ReadEntriesUnsafe(IntPtr entries, ulong count)
    {
        var records = new MftRecord[count];
        byte* basePtr = (byte*)entries;

        if (count >= ParallelThreshold)
        {
            Parallel.For(0L, (long)count, i =>
            {
                byte* ptr = basePtr + i * NativeEntrySize;
                ulong recordNumber = Unsafe.ReadUnaligned<ulong>(ptr);
                ulong parentRecordNumber = Unsafe.ReadUnaligned<ulong>(ptr + 8);
                ushort flags = Unsafe.ReadUnaligned<ushort>(ptr + 16);
                ushort nameLength = Unsafe.ReadUnaligned<ushort>(ptr + 18);
                var fileName = new string((char*)(ptr + 20), 0, nameLength);
                records[i] = new MftRecord(recordNumber, parentRecordNumber, flags, fileName);
            });
        }
        else
        {
            byte* ptr = basePtr;
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
        }

        return records;
    }

    private static unsafe MftRecord[] ReadPathEntriesUnsafe(IntPtr entries, ulong count, string driveLetter)
    {
        var records = new MftRecord[count];
        byte* basePtr = (byte*)entries;

        if (count >= ParallelThreshold)
        {
            Parallel.For(0L, (long)count, i =>
            {
                byte* ptr = basePtr + i * NativePathEntrySize;
                ulong recordNumber = Unsafe.ReadUnaligned<ulong>(ptr);
                ulong parentRecordNumber = Unsafe.ReadUnaligned<ulong>(ptr + 8);
                ushort flags = Unsafe.ReadUnaligned<ushort>(ptr + 16);
                ushort pathLength = Unsafe.ReadUnaligned<ushort>(ptr + 18);
                var relativePath = new string((char*)(ptr + 20), 0, pathLength);
                var fullPath = $"{driveLetter}:\\{relativePath}";
                var lastSep = relativePath.LastIndexOf('\\');
                var fileName = lastSep >= 0 ? relativePath[(lastSep + 1)..] : relativePath;
                records[i] = new MftRecord(recordNumber, parentRecordNumber, flags, fileName, fullPath);
            });
        }
        else
        {
            byte* ptr = basePtr;
            for (ulong i = 0; i < count; i++)
            {
                ulong recordNumber = Unsafe.ReadUnaligned<ulong>(ptr);
                ulong parentRecordNumber = Unsafe.ReadUnaligned<ulong>(ptr + 8);
                ushort flags = Unsafe.ReadUnaligned<ushort>(ptr + 16);
                ushort pathLength = Unsafe.ReadUnaligned<ushort>(ptr + 18);
                var relativePath = new string((char*)(ptr + 20), 0, pathLength);
                var fullPath = $"{driveLetter}:\\{relativePath}";
                var lastSep = relativePath.LastIndexOf('\\');
                var fileName = lastSep >= 0 ? relativePath[(lastSep + 1)..] : relativePath;
                records[i] = new MftRecord(recordNumber, parentRecordNumber, flags, fileName, fullPath);
                ptr += NativePathEntrySize;
            }
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
