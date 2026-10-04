using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MFTLib.Interop;

namespace MFTLib;

/// <summary>
///     The native result of one MFT parse. It owns the native record tables and string pools
///     until disposed. Enumerating it yields records that borrow their strings from that native
///     memory, so they and any open enumerator must not outlive <see cref="Dispose" />; use
///     <see cref="MaterializeBatches" /> or <see cref="ToArray" /> for records that do.
/// </summary>
public sealed class MftResult : IDisposable, IEnumerable<MftRecord>
{
    readonly char _driveLetter;
    readonly MftParseResult _result;
    bool _disposed;
    IntPtr _resultPtr;

    // cancellationToken is the token that could have stopped the parse; a cancelled result throws
    // OperationCanceledException carrying it.
    internal MftResult(IntPtr resultPtr, string driveLetter,
        CancellationToken cancellationToken = default)
    {
        _resultPtr = resultPtr;
        _result = Marshal.PtrToStructure<MftParseResult>(resultPtr);
        _driveLetter = string.IsNullOrEmpty(driveLetter) ? '\0' : driveLetter[0];

        if (_result.Cancelled != 0)
        {
            MFTLibNative._freeMftResult(resultPtr);
            _resultPtr = IntPtr.Zero;
            throw new OperationCanceledException(_result.ErrorMessage, cancellationToken);
        }

        if (!string.IsNullOrEmpty(_result.ErrorMessage))
        {
            MFTLibNative._freeMftResult(resultPtr);
            _resultPtr = IntPtr.Zero;
            throw new InvalidOperationException(_result.ErrorMessage);
        }

        if (_result.AbiVersion != MFTLibNative.ExpectedMftNativeAbiVersion)
        {
            MFTLibNative._freeMftResult(resultPtr);
            _resultPtr = IntPtr.Zero;
            throw new InvalidOperationException(
                $"MFTLib managed/native ABI mismatch: managed expects {MFTLibNative.ExpectedMftNativeAbiVersion}, native reports {_result.AbiVersion}.");
        }

        if (_result.EntryStride != MFTLibNative.NativeCompactEntrySize)
        {
            MFTLibNative._freeMftResult(resultPtr);
            _resultPtr = IntPtr.Zero;
            throw new InvalidOperationException(
                $"MFTLib managed/native ABI mismatch: managed expects stride {MFTLibNative.NativeCompactEntrySize}, native reports {_result.EntryStride}.");
        }

        Timings = new MftParseTimings(
            _result.TotalRecords, _result.IoTimeMs, _result.FixupTimeMs, _result.ParseTimeMs,
            _result.TotalTimeMs, 0);
    }

    /// <summary>
    ///     Total number of MFT records examined during the parse pass.
    ///     Remains readable after <see cref="Dispose" />.
    /// </summary>
    public ulong TotalRecords => _result.TotalRecords;

    /// <summary>
    ///     Number of active or matching records returned in the result set.
    ///     Remains readable after <see cref="Dispose" />.
    /// </summary>
    public ulong UsedRecords => _result.UsedRecords;

    /// <summary>
    ///     Detailed phase timings for the parse pass.
    ///     Remains readable after <see cref="Dispose" />.
    /// </summary>
    public MftParseTimings Timings { get; }

    /// <summary>
    ///     Total bytes occupied by the native compact entry buffers and UTF-16 string pools.
    ///     Cached from the parsed result header and remains readable after <see cref="Dispose" />.
    /// </summary>
    public ulong NativeCompactBytes
    {
        get
        {
            ulong bytes = 0;
            if (_result.Entries != IntPtr.Zero)
            {
                bytes += _result.UsedRecords * MFTLibNative.NativeCompactEntrySize;
            }

            bytes += _result.EntryStringUnits * sizeof(ushort);
            if (_result.PathEntries != IntPtr.Zero)
            {
                bytes += _result.UsedRecords * MFTLibNative.NativeCompactEntrySize;
            }

            bytes += _result.PathStringUnits * sizeof(ushort);
            return bytes;
        }
    }

    /// <summary>
    ///     Frees the native tables and pools. Safe to call more than once. The count and timing
    ///     properties stay readable afterward; enumeration and materialization throw.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            if (_resultPtr != IntPtr.Zero)
            {
                MFTLibNative._freeMftResult(_resultPtr);
                _resultPtr = IntPtr.Zero;
            }

            _disposed = true;
        }
    }

    /// <summary>Enumerates the records in native order, borrowing strings from native memory.</summary>
    /// <returns>An enumerator that is valid only until this result is disposed.</returns>
    /// <exception cref="ObjectDisposedException">This result has been disposed.</exception>
    public IEnumerator<MftRecord> GetEnumerator()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return EnumerateRecords();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator<MftRecord> EnumerateRecords()
    {
        for (ulong i = 0; i < _result.UsedRecords; i++)
        {
            yield return GetValidatedEntry(i);
        }
    }

    unsafe MftRecord GetValidatedEntry(ulong index)
    {
        var active = GetActiveTableAndPool();
        return GetCompactEntry(active.Table, active.Pool, active.PoolUnits, index, active.IsPath, _driveLetter);
    }

    /// <summary>
    ///     Copies the records into managed memory one batch at a time, so a caller holds at most
    ///     one batch of managed strings while the native result stays alive.
    /// </summary>
    /// <param name="batchSize">Records per batch; the last batch may be smaller.</param>
    /// <returns>Batches of materialized records in native order. Enumerate before disposing this result.</returns>
    /// <exception cref="ObjectDisposedException">This result has been disposed; thrown when enumeration starts.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize" /> is not positive; thrown when enumeration starts.</exception>
    public IEnumerable<MftRecord[]> MaterializeBatches(int batchSize = 4096)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        for (ulong start = 0; start < _result.UsedRecords; start += (ulong)batchSize)
        {
            var count = (int)Math.Min((ulong)batchSize, _result.UsedRecords - start);
            var batch = new MftRecord[count];
            for (var i = 0; i < count; i++)
            {
                batch[i] = GetValidatedEntry(start + (ulong)i).Materialize();
            }

            yield return batch;
        }
    }

    /// <summary>Copies every record into one managed array of materialized records.</summary>
    /// <returns>The records, which stay valid after this result is disposed.</returns>
    /// <exception cref="ObjectDisposedException">This result has been disposed.</exception>
    public MftRecord[] ToArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var count = (int)_result.UsedRecords;
        var records = new MftRecord[count];
        var offset = 0;
        foreach (var batch in MaterializeBatches())
        {
            Array.Copy(batch, 0, records, offset, batch.Length);
            offset += batch.Length;
        }

        return records;
    }

    unsafe ActivePool GetActiveTableAndPool()
    {
        if (_result.PathEntries != IntPtr.Zero && _result.PathStrings != IntPtr.Zero)
        {
            return new ActivePool((byte*)_result.PathEntries, (ushort*)_result.PathStrings, _result.PathStringUnits,
                true);
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
        var size = Unsafe.ReadUnaligned<long>(row + 32);
        var modifiedFileTime = Unsafe.ReadUnaligned<long>(row + 40);
        var sequenceNumber = Unsafe.ReadUnaligned<ushort>(row + 48);

        if (stringOffset > poolUnits || stringLength > poolUnits - stringOffset)
        {
            throw new InvalidDataException("Native MFT string offset is outside its pool");
        }

        var pointer = (IntPtr)(pool + stringOffset);
        var strings = isPath
            ? new NativeStrings(IntPtr.Zero, 0, pointer, stringLength)
            : new NativeStrings(pointer, stringLength, IntPtr.Zero, 0);
        var fields = new MftRecordFields(flags, fileAttributes, size, modifiedFileTime, sequenceNumber);
        return new MftRecord(recordNumber, parentRecordNumber, fields, strings, driveLetter);
    }

    readonly unsafe struct ActivePool(byte* table, ushort* pool, ulong poolUnits, bool isPath)
    {
        public readonly byte* Table = table;
        public readonly ushort* Pool = pool;
        public readonly ulong PoolUnits = poolUnits;
        public readonly bool IsPath = isPath;
    }
}
