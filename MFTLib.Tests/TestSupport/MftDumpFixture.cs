using System.Buffers.Binary;
using System.Text;

namespace MFTLib.Tests.TestSupport;

/// <summary>One FILE record of a synthetic dump. The record number is its position in the file.</summary>
sealed record DumpRecord(string Name, ulong Parent)
{
    public bool InUse { get; init; } = true;

    public bool IsDirectory { get; init; }

    /// <summary>The resident data size; null leaves a file without a data attribute, so its size is unknown.</summary>
    public long? Size { get; init; } = 0;

    public long ModifiedFileTime { get; init; } = MftDumpFixture.BaseFileTime;

    public ushort SequenceNumber { get; init; } = 1;

    /// <summary>
    ///     The resident data bytes; when set they replace <see cref="Size" />, which then reads as their length.
    ///     Without them a resident data attribute holds <see cref="Size" /> zero bytes.
    /// </summary>
    public byte[]? Content { get; init; }

    /// <summary>The data attribute is nonresident, with <see cref="Size" /> as its file size.</summary>
    public bool NonResidentData { get; init; }

    /// <summary>The flags word of the data attribute: 1 compressed, 0x4000 encrypted, 0x8000 sparse.</summary>
    public ushort DataFlags { get; init; }

    /// <summary>The record carries a reparse point attribute after its data.</summary>
    public bool ReparsePoint { get; init; }

    /// <summary>The base file reference in the record header: nonzero makes it an extension record.</summary>
    public ulong BaseReference { get; init; }

    /// <summary>The only data attribute is a named stream; the record has no unnamed data.</summary>
    public bool NamedStreamOnly { get; init; }

    /// <summary>How many bytes the data attribute's value length claims beyond what the attribute holds.</summary>
    public uint ValueLengthExcess { get; init; }

    /// <summary>The record carries an <c>$ATTRIBUTE_LIST</c> attribute, as a file whose attributes spill into extension records does.</summary>
    public bool AttributeList { get; init; }

    /// <summary>The record carries a second unnamed data attribute after the first.</summary>
    public bool SecondUnnamedData { get; init; }

    /// <summary>A named data stream precedes the unnamed data attribute.</summary>
    public bool NamedStreamBeforeData { get; init; }

    /// <summary>
    ///     The file attributes word of <c>$STANDARD_INFORMATION</c> and <c>$FILE_NAME</c> also carries
    ///     <c>FILE_ATTRIBUTE_REPARSE_POINT</c>, with no reparse attribute in the record.
    /// </summary>
    public bool ReparseFileAttribute { get; init; }

    /// <summary>The form code the resident unnamed data attribute declares; 0 is resident and 1 nonresident.</summary>
    public byte DataFormCode { get; init; }

    /// <summary>
    ///     The value offset the unnamed data attribute declares; the bytes always sit directly after the 24-byte
    ///     resident header, which is what the default declares.
    /// </summary>
    public ushort ValueOffset { get; init; } = 0x18;

    /// <summary>The record carries no <c>$STANDARD_INFORMATION</c>, as an extension record does not.</summary>
    public bool OmitStandardInformation { get; init; }

    /// <summary>The record carries no <c>$FILE_NAME</c>, as a base whose names spilled into extension records does not.</summary>
    public bool OmitFileName { get; init; }

    /// <summary>The <c>$FILE_NAME</c> namespace: 0 POSIX, 1 Win32, 2 DOS, 3 Win32 and DOS.</summary>
    public byte FileNameNamespace { get; init; } = 1;
}

/// <summary>
///     Builds MFT dump files byte by byte in managed code, so the same fixtures exist on Windows
///     and Linux and a test can then corrupt exactly the field it is about. Every record carries
///     a valid update sequence array unless a test rewrites it.
/// </summary>
static class MftDumpFixture
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

    /// <summary>
    ///     Fills one record: header, <c>$STANDARD_INFORMATION</c>, <c>$FILE_NAME</c>, a resident
    ///     <c>$DATA</c> for a file with a size, the end marker, then the update sequence array.
    /// </summary>
    internal static void WriteRecord(Span<byte> record, DumpRecord values)
    {
        record.Clear();
        var sectorCount = record.Length / SectorSize;
        var firstAttributeOffset = (UpdateSequenceArrayOffset + ((sectorCount + 1) * 2) + 7) & ~7;
        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], UpdateSequenceArrayOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record[6..], (ushort)(sectorCount + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x10..], values.SequenceNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x14..], (ushort)firstAttributeOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x16..],
            (ushort)((values.InUse ? 1 : 0) | (values.IsDirectory ? 2 : 0)));
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x1C..], (uint)record.Length);

        var offset = firstAttributeOffset;
        if (!values.OmitStandardInformation)
        {
            offset += WriteStandardInformation(record[offset..], values);
        }

        if (!values.OmitFileName)
        {
            offset += WriteFileName(record[offset..], values);
        }

        if (values.NamedStreamBeforeData)
        {
            offset += WriteResidentData(record[offset..], values with { Content = [0xAA, 0xBB], ValueLengthExcess = 0 }, name: "ads");
        }

        if (!values.IsDirectory || values.Content is not null)
        {
            offset += WriteData(record[offset..], values);
        }

        if (values.SecondUnnamedData)
        {
            offset += WriteResidentData(record[offset..], values with { Content = [9, 9], ValueLengthExcess = 0 }, name: "");
        }

        if (values.AttributeList)
        {
            offset += WriteAttributeList(record[offset..]);
        }

        if (values.ReparsePoint)
        {
            offset += WriteReparsePoint(record[offset..]);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(record[0x20..], values.BaseReference);

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
            var tail = record.Slice((sector * SectorSize) - 2, 2);
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
        value[65] = values.FileNameNamespace;
        name.CopyTo(value[66..]);
        return length;
    }

    // The data attribute a record carries; none for a file whose size is unknown.
    static int WriteData(Span<byte> attribute, DumpRecord values)
    {
        if (values.NamedStreamOnly)
        {
            return WriteResidentData(attribute, values, name: "ads");
        }

        if (values.NonResidentData)
        {
            return WriteNonResidentData(attribute, values);
        }

        return values.Content is not null || values.Size is not null ? WriteResidentData(attribute, values, name: "") : 0;
    }

    static int WriteResidentData(Span<byte> attribute, DumpRecord values, string name)
    {
        var content = values.Content ?? new byte[values.Size ?? 0];
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var valueStart = 0x18 + nameBytes.Length;
        var length = (valueStart + content.Length + 7) & ~7;
        WriteResidentHeader(attribute, 0x80, length, (uint)content.Length + values.ValueLengthExcess);
        attribute[8] = name.Length == 0 ? values.DataFormCode : (byte)0;
        attribute[9] = (byte)name.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x0A..], 0x18);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x0C..], values.DataFlags);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x14..], name.Length == 0 ? values.ValueOffset : (ushort)valueStart);
        nameBytes.CopyTo(attribute[0x18..]);
        content.CopyTo(attribute[valueStart..]);
        return length;
    }

    static int WriteNonResidentData(Span<byte> attribute, DumpRecord values)
    {
        const int length = 0x48;
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[4..], length);
        attribute[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x0C..], values.DataFlags);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x20..], 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(attribute[0x30..], values.Size ?? 0);
        BinaryPrimitives.WriteInt64LittleEndian(attribute[0x38..], values.Size ?? 0);
        return length;
    }

    static int WriteAttributeList(Span<byte> attribute)
    {
        const int length = 0x20;
        WriteResidentHeader(attribute, 0x20, length, 8);
        return length;
    }

    static int WriteReparsePoint(Span<byte> attribute)
    {
        const int length = 0x20;
        WriteResidentHeader(attribute, 0xC0, length, 8);
        return length;
    }

    static void WriteResidentHeader(Span<byte> attribute, uint typeCode, int length, uint valueLength)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, typeCode);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[4..], (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x10..], valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x14..], 0x18);
    }

    static uint FileAttributesOf(DumpRecord values) =>
        (uint)((values.IsDirectory ? FileAttributes.Directory : FileAttributes.Archive) |
               (values.ReparseFileAttribute ? FileAttributes.ReparsePoint : 0));
}
