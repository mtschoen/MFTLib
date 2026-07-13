using System.Buffers.Binary;
using System.Text;

namespace MFTLib;

public readonly record struct ScanRecord(
    ulong RecordNumber, ulong ParentRecordNumber, ulong Size,
    long LastWriteTicks, uint Attributes, bool IsDirectory,
    string Name, string Path);

/// <summary>
/// Packed binary scan payload for the shared-memory cold-scan handoff:
/// [header][fixed-stride record table][UTF-16 string heap]. The UI reads the
/// table by offset and slices names/paths from the heap, mirroring MFTLib's
/// lazy-materialization design. Replaces the text-on-disk scan round-trip.
/// </summary>
public static class ScanPayload
{
    const uint Magic = 0x4D4C_5343; // "MLSC"
    const int Version = 1;
    const int HeaderSize = 4 + 4 + 4 + 8 + 8; // magic, version, count, tableLen, heapLen
    const int RecordStride = 8 + 8 + 8 + 8 + 4 + 4 + 4 + 4 + 4 + 4; // see field order below

    public static long ComputeSize(IReadOnlyList<ScanRecord> records)
    {
        long heap = 0;
        foreach (var record in records)
            heap += Encoding.Unicode.GetByteCount(record.Name) + Encoding.Unicode.GetByteCount(record.Path);
        return HeaderSize + (long)records.Count * RecordStride + heap;
    }

    public static void Write(Span<byte> destination, IReadOnlyList<ScanRecord> records)
    {
        long tableLen = (long)records.Count * RecordStride;
        var heapStart = HeaderSize + (int)tableLen;
        var heapCursor = heapStart;

        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], records.Count);
        BinaryPrimitives.WriteInt64LittleEndian(destination[12..], tableLen);

        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var rowOffset = HeaderSize + i * RecordStride;
            var row = destination[rowOffset..];
            BinaryPrimitives.WriteUInt64LittleEndian(row, record.RecordNumber);
            BinaryPrimitives.WriteUInt64LittleEndian(row[8..], record.ParentRecordNumber);
            BinaryPrimitives.WriteUInt64LittleEndian(row[16..], record.Size);
            BinaryPrimitives.WriteInt64LittleEndian(row[24..], record.LastWriteTicks);
            BinaryPrimitives.WriteUInt32LittleEndian(row[32..], record.Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(row[36..], record.IsDirectory ? 1u : 0u);

            var nameBytes = Encoding.Unicode.GetBytes(record.Name);
            var pathBytes = Encoding.Unicode.GetBytes(record.Path);
            BinaryPrimitives.WriteInt32LittleEndian(row[40..], heapCursor);
            BinaryPrimitives.WriteInt32LittleEndian(row[44..], nameBytes.Length);
            nameBytes.CopyTo(destination[heapCursor..]); heapCursor += nameBytes.Length;
            BinaryPrimitives.WriteInt32LittleEndian(row[48..], heapCursor);
            BinaryPrimitives.WriteInt32LittleEndian(row[52..], pathBytes.Length);
            pathBytes.CopyTo(destination[heapCursor..]); heapCursor += pathBytes.Length;
        }

        BinaryPrimitives.WriteInt64LittleEndian(destination[20..], heapCursor - heapStart);
    }

    public static int ReadCount(ReadOnlySpan<byte> source)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic)
            throw new InvalidDataException("Scan payload magic mismatch");
        return BinaryPrimitives.ReadInt32LittleEndian(source[8..]);
    }

    public static IEnumerable<ScanRecord> ReadAll(byte[] source)
    {
        var count = ReadCount(source);
        for (var i = 0; i < count; i++)
        {
            var rowOffset = HeaderSize + i * RecordStride;
            var row = source.AsSpan(rowOffset);
            var recordNumber = BinaryPrimitives.ReadUInt64LittleEndian(row);
            var parent = BinaryPrimitives.ReadUInt64LittleEndian(row[8..]);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(row[16..]);
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(row[24..]);
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(row[32..]);
            var isDir = BinaryPrimitives.ReadUInt32LittleEndian(row[36..]) != 0;
            var nameOffset = BinaryPrimitives.ReadInt32LittleEndian(row[40..]);
            var nameLen = BinaryPrimitives.ReadInt32LittleEndian(row[44..]);
            var pathOffset = BinaryPrimitives.ReadInt32LittleEndian(row[48..]);
            var pathLen = BinaryPrimitives.ReadInt32LittleEndian(row[52..]);
            var name = Encoding.Unicode.GetString(source, nameOffset, nameLen);
            var path = Encoding.Unicode.GetString(source, pathOffset, pathLen);
            yield return new ScanRecord(recordNumber, parent, size, ticks, attributes, isDir, name, path);
        }
    }
}
