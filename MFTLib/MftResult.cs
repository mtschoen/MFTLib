using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Interop;

namespace MFTLib;

public sealed class MftResult : IDisposable, IEnumerable<MftRecord>
{
    private IntPtr _resultPtr;
    private readonly MftParseResult _result;
    private readonly char _driveLetter;
    private bool _disposed;

    public ulong TotalRecords => _result.TotalRecords;
    public ulong UsedRecords => _result.UsedRecords;
    public MftParseTimings Timings { get; }

    internal MftResult(IntPtr resultPtr, string driveLetter, double marshalMs)
    {
        _resultPtr = resultPtr;
        _result = Marshal.PtrToStructure<MftParseResult>(resultPtr);
        _driveLetter = string.IsNullOrEmpty(driveLetter) ? '\0' : driveLetter[0];

        Timings = new MftParseTimings(
            _result.TotalRecords, _result.IoTimeMs, _result.FixupTimeMs, _result.ParseTimeMs,
            _result.TotalTimeMs, marshalMs);
    }

    public IEnumerator<MftRecord> GetEnumerator()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_result.PathEntries != IntPtr.Zero)
        {
            for (ulong i = 0; i < _result.UsedRecords; i++)
            {
                yield return GetPathEntry(i);
            }
        }
        else
        {
            for (ulong i = 0; i < _result.UsedRecords; i++)
            {
                yield return GetEntry(i);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private const int ParallelThreshold = 500_000;
    private const int NativeEntrySize = 540;
    private const int NativePathEntrySize = 2068;

    public unsafe MftRecord[] ToArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = _result.UsedRecords;
        var records = new MftRecord[count];

        if (_result.PathEntries != IntPtr.Zero)
        {
            byte* basePtr = (byte*)_result.PathEntries;
            if (count >= ParallelThreshold)
            {
                Parallel.For(0L, (long)count, i => records[i] = GetPathEntryUnsafe(basePtr, (ulong)i).Materialize());
            }
            else
            {
                for (ulong i = 0; i < count; i++) records[i] = GetPathEntryUnsafe(basePtr, i).Materialize();
            }
        }
        else
        {
            byte* basePtr = (byte*)_result.Entries;
            if (count >= ParallelThreshold)
            {
                Parallel.For(0L, (long)count, i => records[i] = GetEntryUnsafe(basePtr, (ulong)i).Materialize());
            }
            else
            {
                for (ulong i = 0; i < count; i++) records[i] = GetEntryUnsafe(basePtr, i).Materialize();
            }
        }

        return records;
    }

    private unsafe MftRecord GetEntry(ulong index) => GetEntryUnsafe((byte*)_result.Entries, index);

    private unsafe MftRecord GetEntryUnsafe(byte* basePtr, ulong index)
    {
        // NativeEntrySize = 540
        byte* ptr = basePtr + index * NativeEntrySize;
        ulong recordNumber = Unsafe.ReadUnaligned<ulong>(ptr);
        ulong parentRecordNumber = Unsafe.ReadUnaligned<ulong>(ptr + 8);
        ushort flags = Unsafe.ReadUnaligned<ushort>(ptr + 16);
        ushort nameLength = Unsafe.ReadUnaligned<ushort>(ptr + 18);
        return new MftRecord(recordNumber, parentRecordNumber, flags, (IntPtr)(ptr + 20), nameLength, default, 0, _driveLetter);
    }

    private unsafe MftRecord GetPathEntry(ulong index) => GetPathEntryUnsafe((byte*)_result.PathEntries, index);

    private unsafe MftRecord GetPathEntryUnsafe(byte* basePtr, ulong index)
    {
        // NativePathEntrySize = 2068
        byte* ptr = basePtr + index * NativePathEntrySize;
        ulong recordNumber = Unsafe.ReadUnaligned<ulong>(ptr);
        ulong parentRecordNumber = Unsafe.ReadUnaligned<ulong>(ptr + 8);
        ushort flags = Unsafe.ReadUnaligned<ushort>(ptr + 16);
        ushort pathLength = Unsafe.ReadUnaligned<ushort>(ptr + 18);
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
