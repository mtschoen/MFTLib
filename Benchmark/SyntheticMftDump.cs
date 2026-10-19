using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Benchmark;

/// <summary>
///     Writes a synthetic MFT dump of 1024-byte FILE records that the native parser accepts: record 0 is
///     <c>$MFT</c>, record 5 is the root, and every later record is a file named after its number under the root.
///     Every record carries a valid update sequence array, so the fixup phase has real work to do.
/// </summary>
internal static class SyntheticMftDump
{
    /// <summary>The fewest records a dump holds, so that record 0 and the root exist.</summary>
    internal const uint MinimumRecords = 16;

    const int RecordSize = 1024;
    const int SectorSize = 512;
    const int UpdateSequenceArrayOffset = 0x30;
    const int RootRecordNumber = 5;
    const long FileTime = 132000000000000000L;
    const ushort UpdateSequenceNumber = 0x0101;

    /// <summary>Writes <paramref name="recordCount" /> records to <paramref name="path" />, one record at a time.</summary>
    internal static void Write(string path, uint recordCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recordCount, MinimumRecords);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var record = new byte[RecordSize];
        for (uint number = 0; number < recordCount; number++)
        {
            Array.Clear(record);
            if (number == 0 || number >= RootRecordNumber)
            {
                var name = number == 0 ? "$MFT" :
                    number == RootRecordNumber ? "." :
                    string.Create(CultureInfo.InvariantCulture, $"file-{number:D10}");
                WriteRecord(record, name, isDirectory: number == RootRecordNumber);
            }

            stream.Write(record);
        }
    }

    static void WriteRecord(Span<byte> record, string name, bool isDirectory)
    {
        var sectorCount = record.Length / SectorSize;
        var firstAttributeOffset = (UpdateSequenceArrayOffset + ((sectorCount + 1) * 2) + 7) & ~7;
        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], UpdateSequenceArrayOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record[6..], (ushort)(sectorCount + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x14..], (ushort)firstAttributeOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x16..], (ushort)(isDirectory ? 3 : 1));
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x1C..], (uint)record.Length);

        var offset = firstAttributeOffset;
        offset += WriteStandardInformation(record[offset..], isDirectory);
        offset += WriteFileName(record[offset..], name, isDirectory);
        BinaryPrimitives.WriteUInt32LittleEndian(record[offset..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x18..], (uint)(offset + 8));
        ProtectSectors(record);
    }

    static int WriteStandardInformation(Span<byte> attribute, bool isDirectory)
    {
        const int valueLength = 48;
        WriteResidentHeader(attribute, 0x10, 0x18 + valueLength, valueLength);
        BinaryPrimitives.WriteInt64LittleEndian(attribute[(0x18 + 8)..], FileTime);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[(0x18 + 32)..], AttributesOf(isDirectory));
        return 0x18 + valueLength;
    }

    static int WriteFileName(Span<byte> attribute, string name, bool isDirectory)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var valueLength = 66 + nameBytes.Length;
        var length = (0x18 + valueLength + 7) & ~7;
        WriteResidentHeader(attribute, 0x30, length, valueLength);
        var value = attribute[0x18..];
        BinaryPrimitives.WriteUInt32LittleEndian(value, RootRecordNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(value[6..], 1);
        BinaryPrimitives.WriteInt64LittleEndian(value[16..], FileTime);
        BinaryPrimitives.WriteUInt32LittleEndian(value[56..], AttributesOf(isDirectory));
        value[64] = (byte)name.Length;
        value[65] = 1;
        nameBytes.CopyTo(value[66..]);
        return length;
    }

    static void WriteResidentHeader(Span<byte> attribute, uint typeCode, int length, int valueLength)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, typeCode);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[4..], (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x10..], (uint)valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(attribute[0x14..], 0x18);
    }

    static uint AttributesOf(bool isDirectory) => (uint)(isDirectory ? FileAttributes.Directory : FileAttributes.Archive);

    // Moves each sector's last word into the update sequence array and stamps the sequence number there, as NTFS does.
    static void ProtectSectors(Span<byte> record)
    {
        var array = record[UpdateSequenceArrayOffset..];
        BinaryPrimitives.WriteUInt16LittleEndian(array, UpdateSequenceNumber);
        for (var sector = 1; sector <= record.Length / SectorSize; sector++)
        {
            var tail = record.Slice((sector * SectorSize) - 2, 2);
            tail.CopyTo(array[(sector * 2)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(tail, UpdateSequenceNumber);
        }
    }
}
