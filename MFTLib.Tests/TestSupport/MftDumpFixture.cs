using System.Buffers.Binary;
using System.Text;

namespace MFTLib.Tests.TestSupport;

/// <summary>One FILE record of a synthetic dump. The record number is its position in the file.</summary>
internal sealed record DumpRecord(string Name, ulong Parent)
{
    public bool InUse { get; init; } = true;

    public bool IsDirectory { get; init; }

    /// <summary>The resident data size; null leaves a file without a data attribute, so its size is unknown.</summary>
    public long? Size { get; init; } = 0;

    public long ModifiedFileTime { get; init; } = MftDumpFixture.BaseFileTime;

    public ushort SequenceNumber { get; init; } = 1;
}

/// <summary>
///     Builds MFT dump files byte by byte in managed code, so the same fixtures exist on Windows
///     and Linux and a test can then corrupt exactly the field it is about. Every record carries
///     a valid update sequence array unless a test rewrites it.
/// </summary>
internal static class MftDumpFixture
{
    internal const long BaseFileTime = 132000000000000000L;

    internal const int UpdateSequenceArrayOffset = 0x30;

    const int SectorSize = 512;

    /// <summary>
    ///     The standard tree: 0 <c>$MFT</c>, 5 the root, 6 <c>documents</c>, 7 <c>Notes.txt</c> (11 bytes)
    ///     and 8 <c>Deep</c> under 6, 9 <c>leaf.txt</c> (22 bytes) under 8, and 10 <c>sizeless.bin</c>
    ///     under 5, which has no data attribute.
    /// </summary>
    internal static Dictionary<int, DumpRecord> StandardRecords() => new()
    {
        [0] = new DumpRecord("$MFT", 5),
        [5] = new DumpRecord(".", 5) { IsDirectory = true },
        [6] = new DumpRecord("documents", 5) { IsDirectory = true },
        [7] = new DumpRecord("Notes.txt", 6) { Size = 11, ModifiedFileTime = BaseFileTime + 10_000_000 },
        [8] = new DumpRecord("Deep", 6) { IsDirectory = true },
        [9] = new DumpRecord("leaf.txt", 8) { Size = 22 },
        [10] = new DumpRecord("sizeless.bin", 5) { Size = null }
    };

    /// <summary>The standard tree as a dump of sixteen records.</summary>
    internal static byte[] Standard(int recordSize = 1024) => Build(recordSize, 16, StandardRecords());

    /// <summary>A dump of <paramref name="recordCount" /> records; a position with no record stays zeroed.</summary>
    internal static byte[] Build(int recordSize, int recordCount, IReadOnlyDictionary<int, DumpRecord> records)
    {
        var dump = new byte[recordSize * recordCount];
        foreach (var (recordNumber, record) in records)
        {
            WriteRecord(dump.AsSpan(recordNumber * recordSize, recordSize), record);
        }

        return dump;
    }

    /// <summary>Writes the dump to a new file under <paramref name="directory" /> and returns its path.</summary>
    internal static string WriteFile(string directory, byte[] dump, string name = "volume.mft")
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, dump);
        return path;
    }

    /// <summary>A new directory under the temporary path that the calling test owns and deletes.</summary>
    internal static string NewOwnedDirectory() =>
        Path.Combine(Path.GetTempPath(), $"mftlib-dump-{Guid.NewGuid():N}");

    internal static void DeleteOwnedDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A just-unmapped block file can stay locked briefly on Windows.
        }
    }

    /// <summary>
    ///     Fills one record: header, <c>$STANDARD_INFORMATION</c>, <c>$FILE_NAME</c>, a resident
    ///     <c>$DATA</c> for a file with a size, the end marker, then the update sequence array.
    /// </summary>
    internal static void WriteRecord(Span<byte> record, DumpRecord values)
    {
        record.Clear();
        var sectorCount = record.Length / SectorSize;
        var firstAttributeOffset = (UpdateSequenceArrayOffset + (sectorCount + 1) * 2 + 7) & ~7;
        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], UpdateSequenceArrayOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record[6..], (ushort)(sectorCount + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x10..], values.SequenceNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x14..], (ushort)firstAttributeOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x16..],
            (ushort)((values.InUse ? 1 : 0) | (values.IsDirectory ? 2 : 0)));
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x1C..], (uint)record.Length);

        var offset = firstAttributeOffset;
        offset += WriteStandardInformation(record[offset..], values);
        offset += WriteFileName(record[offset..], values);
        if (!values.IsDirectory && values.Size is { } size)
        {
            offset += WriteResidentData(record[offset..], size);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record[offset..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x18..], (uint)(offset + 8));
        ProtectSectors(record, updateSequenceNumber: 0x0101);
    }

    /// <summary>
    ///     Moves each sector's last word into the update sequence array and stamps the update
    ///     sequence number in its place, as NTFS does before writing a record.
    /// </summary>
    internal static void ProtectSectors(Span<byte> record, ushort updateSequenceNumber)
    {
        var array = record[UpdateSequenceArrayOffset..];
        BinaryPrimitives.WriteUInt16LittleEndian(array, updateSequenceNumber);
        for (var sector = 1; sector <= record.Length / SectorSize; sector++)
        {
            var tail = record.Slice(sector * SectorSize - 2, 2);
            tail.CopyTo(array[(sector * 2)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(tail, updateSequenceNumber);
        }
    }

    /// <summary>The byte offset of a record's first attribute, read from its header.</summary>
    internal static int FirstAttributeOffset(ReadOnlySpan<byte> record) =>
        BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);

    static int WriteStandardInformation(Span<byte> attribute, DumpRecord values)
    {
        const uint valueLength = 48;
        WriteResidentHeader(attribute, 0x10, 0x18 + (int)valueLength, valueLength);
        var value = attribute[0x18..];
        BinaryPrimitives.WriteInt64LittleEndian(value[8..], values.ModifiedFileTime);
        BinaryPrimitives.WriteUInt32LittleEndian(value[32..], FileAttributesOf(values));
        return 0x18 + (int)valueLength;
    }

    static int WriteFileName(Span<byte> attribute, DumpRecord values)
    {
        var name = Encoding.Unicode.GetBytes(values.Name);
        var valueLength = 66 + name.Length;
        var length = (0x18 + valueLength + 7) & ~7;
        WriteResidentHeader(attribute, 0x30, length, (uint)valueLength);
        var value = attribute[0x18..];
        BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)(values.Parent & 0xFFFFFFFF));
        BinaryPrimitives.WriteUInt16LittleEndian(value[4..], (ushort)(values.Parent >> 32));
        BinaryPrimitives.WriteUInt16LittleEndian(value[6..], 1);
        BinaryPrimitives.WriteInt64LittleEndian(value[16..], values.ModifiedFileTime);
        BinaryPrimitives.WriteUInt32LittleEndian(value[56..], FileAttributesOf(values));
        value[64] = (byte)values.Name.Length;
        value[65] = 1;
        name.CopyTo(value[66..]);
        return length;
    }

    static int WriteResidentData(Span<byte> attribute, long size)
    {
        WriteResidentHeader(attribute, 0x80, 0x18, (uint)size);
        return 0x18;
    }

    static void WriteResidentHeader(Span<byte> attribute, uint typeCode, int length, uint valueLength)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, typeCode);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[4..], (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x10..], valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x14..], 0x18);
    }

    static uint FileAttributesOf(DumpRecord values) =>
        (uint)(values.IsDirectory ? FileAttributes.Directory : FileAttributes.Archive);
}
