using System.Buffers.Binary;
using System.Text;

namespace MFTLib;

public readonly record struct ScanRecord(
    ulong RecordNumber,
    ulong ParentRecordNumber,
    ulong Size,
    long LastWriteTicks,
    uint Attributes,
    bool IsDirectory,
    string Name,
    string Path);

/// <summary>
///     Packed binary scan payload for the shared-memory cold-scan handoff (Version 2).
///     Uses an interleaved one-pass binary layout:
///     [magic u32][version i32=2][recordCount i64][byteLength i64]
///     repeated recordCount times:
///     [recordNumber u64][parent u64][size u64][lastWriteTicks i64]
///     [attributes u32][isDirectory u8][reserved u8,u16]
///     [nameByteLength i32][pathByteLength i32][name UTF-16][path UTF-16]
/// </summary>
public static class ScanPayload
{
    const uint Magic = 0x4D4C_5343; // "MLSC"
    const int Version = 2;
    public const int HeaderSize = 4 + 4 + 8 + 8; // magic (4), version (4), count (8), byteLength (8) = 24
    public const int FixedRecordHeaderSize = 8 + 8 + 8 + 8 + 4 + 1 + 1 + 2 + 4 + 4; // 48 bytes

    public static long ComputeSize(IReadOnlyList<ScanRecord> records)
    {
        long size = HeaderSize;
        foreach (var record in records)
        {
            size += FixedRecordHeaderSize +
                    Encoding.Unicode.GetByteCount(record.Name) +
                    Encoding.Unicode.GetByteCount(record.Path);
        }

        return size;
    }

    public static void Write(Span<byte> destination, IReadOnlyList<ScanRecord> records)
    {
        var totalBytes = ComputeSize(records);
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], Version);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], records.Count);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], totalBytes);

        var cursor = HeaderSize;
        foreach (var record in records)
        {
            var row = destination.Slice(cursor, FixedRecordHeaderSize);
            BinaryPrimitives.WriteUInt64LittleEndian(row, record.RecordNumber);
            BinaryPrimitives.WriteUInt64LittleEndian(row[8..], record.ParentRecordNumber);
            BinaryPrimitives.WriteUInt64LittleEndian(row[16..], record.Size);
            BinaryPrimitives.WriteInt64LittleEndian(row[24..], record.LastWriteTicks);
            BinaryPrimitives.WriteUInt32LittleEndian(row[32..], record.Attributes);
            row[36] = (byte)(record.IsDirectory ? 1 : 0);
            row[37] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(row[38..], 0);

            var nameBytes = Encoding.Unicode.GetBytes(record.Name);
            var pathBytes = Encoding.Unicode.GetBytes(record.Path);

            BinaryPrimitives.WriteInt32LittleEndian(row[40..], nameBytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(row[44..], pathBytes.Length);
            cursor += FixedRecordHeaderSize;

            nameBytes.CopyTo(destination[cursor..]);
            cursor += nameBytes.Length;

            pathBytes.CopyTo(destination[cursor..]);
            cursor += pathBytes.Length;
        }
    }

    public static MmfWriteResult Write(
        Stream destination,
        IEnumerable<IReadOnlyList<ScanRecord>> batches,
        CancellationToken cancellationToken)
    {
        return Write(destination, batches, null, cancellationToken);
    }

    public static MmfWriteResult Write(
        Stream destination,
        IEnumerable<IReadOnlyList<ScanRecord>> batches,
        IProgress<MmfWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startPosition = destination.Position;
        Span<byte> headerBytes = stackalloc byte[HeaderSize];
        destination.Write(headerBytes);

        long recordCount = 0;
        Span<byte> recordHeader = stackalloc byte[FixedRecordHeaderSize];

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var record in batch)
            {
                recordCount++;

                BinaryPrimitives.WriteUInt64LittleEndian(recordHeader, record.RecordNumber);
                BinaryPrimitives.WriteUInt64LittleEndian(recordHeader[8..], record.ParentRecordNumber);
                BinaryPrimitives.WriteUInt64LittleEndian(recordHeader[16..], record.Size);
                BinaryPrimitives.WriteInt64LittleEndian(recordHeader[24..], record.LastWriteTicks);
                BinaryPrimitives.WriteUInt32LittleEndian(recordHeader[32..], record.Attributes);
                recordHeader[36] = (byte)(record.IsDirectory ? 1 : 0);
                recordHeader[37] = 0;
                BinaryPrimitives.WriteUInt16LittleEndian(recordHeader[38..], 0);

                var nameBytes = Encoding.Unicode.GetBytes(record.Name);
                var pathBytes = Encoding.Unicode.GetBytes(record.Path);
                BinaryPrimitives.WriteInt32LittleEndian(recordHeader[40..], nameBytes.Length);
                BinaryPrimitives.WriteInt32LittleEndian(recordHeader[44..], pathBytes.Length);

                destination.Write(recordHeader);
                destination.Write(nameBytes);
                destination.Write(pathBytes);
            }

            var currentBytes = destination.Position - startPosition;
            progress?.Report(new MmfWriteProgress(recordCount, currentBytes, null, null));
        }

        var endPosition = destination.Position;
        var totalBytes = endPosition - startPosition;

        destination.Position = startPosition;
        BinaryPrimitives.WriteUInt32LittleEndian(headerBytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(headerBytes[4..], Version);
        BinaryPrimitives.WriteInt64LittleEndian(headerBytes[8..], recordCount);
        BinaryPrimitives.WriteInt64LittleEndian(headerBytes[16..], totalBytes);
        destination.Write(headerBytes);

        destination.Position = endPosition;
        destination.Flush();

        progress?.Report(new MmfWriteProgress(recordCount, totalBytes, recordCount, totalBytes));

        return new MmfWriteResult(recordCount, totalBytes);
    }


    public static long ReadCount(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
        {
            throw new InvalidDataException("Scan payload header too short");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic)
        {
            throw new InvalidDataException("Scan payload magic mismatch");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(source[4..]);
        if (version != Version)
        {
            throw new InvalidDataException($"Scan payload version mismatch: expected {Version}, got {version}");
        }

        var count = BinaryPrimitives.ReadInt64LittleEndian(source[8..]);
        if (count < 0)
        {
            throw new InvalidDataException("Negative record count in scan payload");
        }

        return count;
    }

    public static IEnumerable<ScanRecord[]> ReadBatches(
        Stream source,
        long byteLength,
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);

        if (byteLength < HeaderSize)
        {
            throw new InvalidDataException($"Payload byteLength {byteLength} is smaller than header size {HeaderSize}");
        }

        var recordCount = ReadAndValidateHeader(source, byteLength);
        if (recordCount == 0)
        {
            yield break;
        }

        var currentBatch = new List<ScanRecord>(Math.Min(batchSize, 4096));
        var fixedHeaderBuffer = new byte[FixedRecordHeaderSize];
        long currentOffset = HeaderSize;

        for (long i = 0; i < recordCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = ReadRecord(source, fixedHeaderBuffer, i, ref currentOffset, byteLength);
            currentBatch.Add(record);

            if (currentBatch.Count >= batchSize)
            {
                yield return currentBatch.ToArray();
                currentBatch.Clear();
            }
        }

        if (currentOffset != byteLength)
        {
            throw new InvalidDataException($"Trailing or incomplete data in payload: offset is {currentOffset}, expected {byteLength}");
        }

        if (currentBatch.Count > 0)
        {
            yield return currentBatch.ToArray();
        }
    }

    static long ReadAndValidateHeader(Stream source, long expectedByteLength)
    {
        var header = new byte[HeaderSize];
        var readHeaderBytes = ReadExactly(source, header, HeaderSize);
        if (readHeaderBytes < HeaderSize)
        {
            throw new InvalidDataException("Truncated scan payload header");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
        {
            throw new InvalidDataException("Scan payload magic mismatch");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (version != Version)
        {
            throw new InvalidDataException($"Scan payload version mismatch: expected {Version}, got {version}");
        }

        var recordCount = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
        if (recordCount < 0)
        {
            throw new InvalidDataException("Negative record count in scan payload");
        }

        var payloadByteLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
        if (payloadByteLength != expectedByteLength)
        {
            throw new InvalidDataException($"Scan payload byte length mismatch: header has {payloadByteLength}, expected {expectedByteLength}");
        }

        return recordCount;
    }

    static ScanRecord ReadRecord(
        Stream source,
        byte[] fixedHeaderBuffer,
        long recordIndex,
        ref long currentOffset,
        long totalByteLength)
    {
        if (currentOffset + FixedRecordHeaderSize > totalByteLength)
        {
            throw new InvalidDataException("Record header exceeds payload byte length");
        }

        var read = ReadExactly(source, fixedHeaderBuffer, FixedRecordHeaderSize);
        if (read < FixedRecordHeaderSize)
        {
            throw new InvalidDataException("Truncated record header in scan payload");
        }

        var span = fixedHeaderBuffer.AsSpan();
        var recordNumber = BinaryPrimitives.ReadUInt64LittleEndian(span);
        var parent = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
        var size = BinaryPrimitives.ReadUInt64LittleEndian(span[16..]);
        var lastWriteTicks = BinaryPrimitives.ReadInt64LittleEndian(span[24..]);
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(span[32..]);
        var isDir = span[36] != 0;

        var nameByteLength = BinaryPrimitives.ReadInt32LittleEndian(span[40..]);
        var pathByteLength = BinaryPrimitives.ReadInt32LittleEndian(span[44..]);

        if (nameByteLength < 0 || (nameByteLength % 2) != 0)
        {
            throw new InvalidDataException($"Invalid name byte length {nameByteLength} in record {recordIndex}");
        }

        if (pathByteLength < 0 || (pathByteLength % 2) != 0)
        {
            throw new InvalidDataException($"Invalid path byte length {pathByteLength} in record {recordIndex}");
        }

        if (currentOffset + FixedRecordHeaderSize + nameByteLength + pathByteLength > totalByteLength)
        {
            throw new InvalidDataException("Record string data exceeds payload byte length");
        }

        var nameBytes = new byte[nameByteLength];
        if (nameByteLength > 0 && ReadExactly(source, nameBytes, nameByteLength) < nameByteLength)
        {
            throw new InvalidDataException("Truncated record name in scan payload");
        }

        var pathBytes = new byte[pathByteLength];
        if (pathByteLength > 0 && ReadExactly(source, pathBytes, pathByteLength) < pathByteLength)
        {
            throw new InvalidDataException("Truncated record path in scan payload");
        }

        currentOffset += FixedRecordHeaderSize + nameByteLength + pathByteLength;

        var name = Encoding.Unicode.GetString(nameBytes);
        var path = Encoding.Unicode.GetString(pathBytes);

        return new ScanRecord(recordNumber, parent, size, lastWriteTicks, attributes, isDir, name, path);
    }

    static int ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, totalRead, count - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    public static IEnumerable<ScanRecord> ReadAll(byte[] source)
    {
        using var stream = new MemoryStream(source, writable: false);
        foreach (var batch in ReadBatches(stream, source.Length, 4096, CancellationToken.None))
        {
            foreach (var record in batch)
            {
                yield return record;
            }
        }
    }
}
