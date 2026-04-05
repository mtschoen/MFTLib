using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Interop;

namespace MFTLib;

public sealed class MftResult : IDisposable, IEnumerable<MftRecord>
{
    IntPtr _resultPtr;
    readonly MftParseResult _result;
    readonly char _driveLetter;
    bool _disposed;

    public ulong TotalRecords => _result.TotalRecords;
    public ulong UsedRecords => _result.UsedRecords;
    public MftParseTimings Timings { get; }

    internal MftResult(IntPtr resultPtr, string driveLetter, double marshalMs)
    {
        _resultPtr = resultPtr;
        _result = Marshal.PtrToStructure<MftParseResult>(resultPtr);
        _driveLetter = string.IsNullOrEmpty(driveLetter) ? '\0' : driveLetter[0];

        if (!string.IsNullOrEmpty(_result.ErrorMessage))
        {
            MFTLibNative.FreeMftResult(resultPtr);
            _resultPtr = IntPtr.Zero;
            throw new InvalidOperationException(_result.ErrorMessage);
        }

        Timings = new MftParseTimings(
            _result.TotalRecords, _result.IoTimeMs, _result.FixupTimeMs, _result.ParseTimeMs,
            _result.TotalTimeMs, marshalMs);
    }

    public IEnumerator<MftRecord> GetEnumerator()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (ulong i = 0; i < _result.UsedRecords; i++)
        {
            yield return _result.PathEntries != IntPtr.Zero ? GetPathEntry(i) : GetEntry(i);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static int ParallelThreshold = 500_000;
    const int NativeEntrySize = 540;
    const int NativePathEntrySize = 2068;

    unsafe delegate MftRecord EntryReader(byte* basePtr, ulong index);

    public unsafe MftRecord[] ToArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = _result.UsedRecords;
        var records = new MftRecord[count];

        byte* basePtr;
        EntryReader getEntry;
        if (_result.PathEntries != IntPtr.Zero)
        {
            basePtr = (byte*)_result.PathEntries;
            getEntry = GetPathEntryUnsafe;
        }
        else
        {
            basePtr = (byte*)_result.Entries;
            getEntry = GetEntryUnsafe;
        }

        if (count >= (ulong)ParallelThreshold)
            Parallel.For(0L, (long)count, i => records[i] = getEntry(basePtr, (ulong)i).Materialize());
        else
            for (ulong i = 0; i < count; i++) records[i] = getEntry(basePtr, i).Materialize();

        return records;
    }

    unsafe MftRecord GetEntry(ulong index) => GetEntryUnsafe((byte*)_result.Entries, index);

    unsafe MftRecord GetEntryUnsafe(byte* basePtr, ulong index)
    {
        var ptr = basePtr + index * NativeEntrySize;
        var recordNumber = Unsafe.ReadUnaligned<ulong>(ptr);
        var parentRecordNumber = Unsafe.ReadUnaligned<ulong>(ptr + 8);
        var flags = Unsafe.ReadUnaligned<ushort>(ptr + 16);
        var nameLength = Unsafe.ReadUnaligned<ushort>(ptr + 18);
        // ReSharper disable once PreferConcreteValueOverDefault
        return new MftRecord(recordNumber, parentRecordNumber, flags, (IntPtr)(ptr + 20), nameLength, default, 0, _driveLetter);
    }

    unsafe MftRecord GetPathEntry(ulong index) => GetPathEntryUnsafe((byte*)_result.PathEntries, index);

    unsafe MftRecord GetPathEntryUnsafe(byte* basePtr, ulong index)
    {
        var ptr = basePtr + index * NativePathEntrySize;
        var recordNumber = Unsafe.ReadUnaligned<ulong>(ptr);
        var parentRecordNumber = Unsafe.ReadUnaligned<ulong>(ptr + 8);
        var flags = Unsafe.ReadUnaligned<ushort>(ptr + 16);
        var pathLength = Unsafe.ReadUnaligned<ushort>(ptr + 18);
        return new MftRecord(recordNumber, parentRecordNumber, flags, IntPtr.Zero, 0, (IntPtr)(ptr + 20), pathLength, _driveLetter);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_resultPtr != IntPtr.Zero)
            {
                MFTLibNative.FreeMftResult(_resultPtr);
                _resultPtr = IntPtr.Zero;
            }
            _disposed = true;
        }
    }
}
