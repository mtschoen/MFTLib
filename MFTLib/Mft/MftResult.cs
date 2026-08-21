using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Interop;

namespace MFTLib;

public sealed class MftResult : IDisposable, IEnumerable<MftRecord>
{
    internal static int ParallelThreshold { get; set; } = 500_000;
    readonly char _driveLetter;
    readonly MftParseResult _result;
    bool _disposed;
    IntPtr _resultPtr;

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

        if (_result.AbiVersion != MFTLibNative.ExpectedMftNativeAbiVersion)
        {
            MFTLibNative.FreeMftResult(resultPtr);
            _resultPtr = IntPtr.Zero;
            throw new InvalidOperationException(
                $"MFTLib managed/native ABI mismatch: managed expects {MFTLibNative.ExpectedMftNativeAbiVersion}, native reports {_result.AbiVersion}.");
        }

        if (_result.EntryStride != MFTLibNative.NativeCompactEntrySize)
        {
            MFTLibNative.FreeMftResult(resultPtr);
            _resultPtr = IntPtr.Zero;
            throw new InvalidOperationException(
                $"MFTLib managed/native ABI mismatch: managed expects stride {MFTLibNative.NativeCompactEntrySize}, native reports {_result.EntryStride}.");
        }

        Timings = new MftParseTimings(
            _result.TotalRecords, _result.IoTimeMs, _result.FixupTimeMs, _result.ParseTimeMs,
            _result.TotalTimeMs, marshalMs);
    }

    public ulong TotalRecords => _result.TotalRecords;
    public ulong UsedRecords => _result.UsedRecords;
    public MftParseTimings Timings { get; }

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

    public IEnumerator<MftRecord> GetEnumerator()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return EnumerateRecords();
    }

    IEnumerator<MftRecord> EnumerateRecords()
    {
        for (ulong i = 0; i < _result.UsedRecords; i++)
        {
            yield return GetEntry(i);
        }
    }

    unsafe MftRecord GetEntry(ulong index)
    {
        var active = GetActiveTableAndPool();
        return GetCompactEntry(active.Table, active.Pool, active.PoolUnits, index, active.IsPath, _driveLetter);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public unsafe MftRecord[] ToArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = _result.UsedRecords;
        var records = new MftRecord[count];

        var active = GetActiveTableAndPool();
        var driveLetter = _driveLetter;

        if (count >= (ulong)ParallelThreshold)
        {
            Parallel.For(0L, (long)count, i => records[i] = GetCompactEntry(active.Table, active.Pool, active.PoolUnits, (ulong)i, active.IsPath, driveLetter).Materialize());
        }
        else
        {
            for (ulong i = 0; i < count; i++)
            {
                records[i] = GetCompactEntry(active.Table, active.Pool, active.PoolUnits, i, active.IsPath, driveLetter).Materialize();
            }
        }

        return records;
    }

    readonly unsafe struct ActivePool(byte* table, ushort* pool, ulong poolUnits, bool isPath)
    {
        public readonly byte* Table = table;
        public readonly ushort* Pool = pool;
        public readonly ulong PoolUnits = poolUnits;
        public readonly bool IsPath = isPath;
    }

    unsafe ActivePool GetActiveTableAndPool()
    {
        if (_result.PathEntries != IntPtr.Zero && _result.PathStrings != IntPtr.Zero)
        {
            return new ActivePool((byte*)_result.PathEntries, (ushort*)_result.PathStrings, _result.PathStringUnits, true);
        }
        return new ActivePool((byte*)_result.Entries, (ushort*)_result.EntryStrings, _result.EntryStringUnits, false);
    }

    static unsafe MftRecord GetCompactEntry(
        byte* table, ushort* pool, ulong poolUnits, ulong index, bool isPath, char driveLetter)
    {
        var row = table + checked((nuint)index * MFTLibNative.NativeCompactEntrySize);
        var recordNumber = Unsafe.ReadUnaligned<ulong>(row);
        var parentRecordNumber = Unsafe.ReadUnaligned<ulong>(row + 8);
        var stringOffset = Unsafe.ReadUnaligned<ulong>(row + 16);
        var fileAttributes = (FileAttributes)Unsafe.ReadUnaligned<uint>(row + 24);
        var flags = Unsafe.ReadUnaligned<ushort>(row + 28);
        var stringLength = Unsafe.ReadUnaligned<ushort>(row + 30);

        if (stringOffset > poolUnits || stringLength > poolUnits - stringOffset)
        {
            throw new InvalidDataException("Native MFT string offset is outside its pool");
        }

        var pointer = stringLength == 0 ? IntPtr.Zero : (IntPtr)(pool + stringOffset);
        var strings = isPath
            ? new NativeStrings(IntPtr.Zero, 0, pointer, stringLength)
            : new NativeStrings(pointer, stringLength, IntPtr.Zero, 0);
        return new MftRecord(recordNumber, parentRecordNumber, flags, fileAttributes, strings, driveLetter);
    }
}
