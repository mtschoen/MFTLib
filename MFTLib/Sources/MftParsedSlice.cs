using System.Buffers;
using System.Runtime.CompilerServices;
using MFTLib.NativeInterop;

namespace MFTLib;

/// <summary>
///     One parsed slice in the native layout: packed <c>MftCompactEntry</c> rows and the UTF-16 units their string
///     offsets count from. The arrays are pooled and may be longer than the counts; <see cref="Return" /> gives them
///     back once the slice is written.
/// </summary>
sealed class MftParsedSlice
{
    MftParsedSlice(int entryCount, int stringUnits)
    {
        EntryCount = entryCount;
        StringUnits = stringUnits;
        Entries = ArrayPool<byte>.Shared.Rent(Math.Max(entryCount * (int)MFTLibNative.NativeCompactEntrySize, 1));
        Strings = ArrayPool<char>.Shared.Rent(Math.Max(stringUnits, 1));
    }

    internal byte[] Entries { get; }

    internal char[] Strings { get; }

    internal int EntryCount { get; }

    internal int StringUnits { get; }

    /// <summary>Copies a slice the native parse handed its sink, which is valid only during that call.</summary>
    internal static unsafe MftParsedSlice CopyFromNative(byte* entries, ulong entryCount, ushort* strings,
        ulong stringUnits)
    {
        var slice = new MftParsedSlice(checked((int)entryCount), checked((int)stringUnits));
        new ReadOnlySpan<byte>(entries, checked(slice.EntryCount * (int)MFTLibNative.NativeCompactEntrySize))
            .CopyTo(slice.Entries);
        new ReadOnlySpan<char>(strings, slice.StringUnits).CopyTo(slice.Strings);
        return slice;
    }

    /// <summary>
    ///     Packs materialized records into the native layout, so a source of records is written by the same writer as
    ///     a native parse. Every field round-trips: reading the packed entry back gives the record.
    /// </summary>
    /// <exception cref="ArgumentException">A name is longer than a packed entry's 16-bit name length.</exception>
    internal static MftParsedSlice Pack(IReadOnlyList<MftRecord> records)
    {
        var stringUnits = 0;
        foreach (var record in records)
        {
            if (record.FileName.Length > ushort.MaxValue)
            {
                throw new ArgumentException($"Record {record.RecordNumber} has a name longer than {ushort.MaxValue} units.",
                    nameof(records));
            }

            stringUnits += record.FileName.Length;
        }

        var slice = new MftParsedSlice(records.Count, stringUnits);
        var stringOffset = 0;
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var row = slice.Entries.AsSpan(index * (int)MFTLibNative.NativeCompactEntrySize);
            Unsafe.WriteUnaligned(ref row[0], record.RecordNumber);
            Unsafe.WriteUnaligned(ref row[8], record.ParentRecordNumber);
            Unsafe.WriteUnaligned(ref row[16], (ulong)stringOffset);
            Unsafe.WriteUnaligned(ref row[24], (uint)record.FileAttributes);
            Unsafe.WriteUnaligned(ref row[28], record.Flags);
            Unsafe.WriteUnaligned(ref row[30], (ushort)record.FileName.Length);
            Unsafe.WriteUnaligned(ref row[32], record.Size);
            Unsafe.WriteUnaligned(ref row[40], record.ModifiedFileTime);
            Unsafe.WriteUnaligned(ref row[48], record.SequenceNumber);
            Unsafe.WriteUnaligned(ref row[50], record.ParentSequenceNumber);
            record.FileName.CopyTo(slice.Strings.AsSpan(stringOffset));
            stringOffset += record.FileName.Length;
        }

        return slice;
    }

    internal void Return()
    {
        ArrayPool<byte>.Shared.Return(Entries);
        ArrayPool<char>.Shared.Return(Strings);
    }
}
