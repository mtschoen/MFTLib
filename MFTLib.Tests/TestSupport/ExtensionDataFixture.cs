using System.Buffers.Binary;

namespace MFTLib.Tests.TestSupport;

/// <summary>A base with an attribute list and an unnamed data attribute in a separate FILE record.</summary>
static class ExtensionDataFixture
{
    internal const long DataSize = 123456789;

    internal sealed record Options
    {
        internal bool Resident { get; init; }
        internal long LowestVcn { get; init; }
        internal ushort BaseSequenceNumber { get; init; } = 1;
        internal bool InUse { get; init; } = true;
        internal bool Named { get; init; }
        internal bool Truncated { get; init; }
        internal bool MalformedTail { get; init; }
        internal bool MissingData { get; init; }
        internal ulong? ReferencedRecordNumber { get; init; }
        internal long Size { get; init; } = DataSize;
        internal DumpRecord Base { get; init; } = new("linked.bin", 5) { Size = null };
    }

    internal static byte[] Build(int baseRecordNumber = 10, int extensionRecordNumber = 24,
        int recordSize = 1024, Options? options = null)
    {
        options ??= new Options();
        var records = MftDumpFixture.StandardRecords();
        records[baseRecordNumber] = options.Base;
        records[extensionRecordNumber] = new DumpRecord("", 5)
        {
            InUse = options.InUse,
            Size = null,
            SequenceNumber = 17
        };
        var dump = MftDumpFixture.Build(recordSize, 32, records);
        var baseRecord = dump.AsSpan(baseRecordNumber * recordSize, recordSize);
        var listOffset = EndOfAttributes(baseRecord);
        var list = baseRecord[listOffset..];
        list[..64].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(list, 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(list[4..], 56);
        BinaryPrimitives.WriteUInt32LittleEndian(list[16..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(list[20..], 24);
        BinaryPrimitives.WriteUInt32LittleEndian(list[24..], 0x80);
        BinaryPrimitives.WriteUInt16LittleEndian(list[28..], 32);
        BinaryPrimitives.WriteUInt64LittleEndian(list[40..], (17UL << 48) | (uint)extensionRecordNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(list[56..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(baseRecord[0x18..], (uint)(listOffset + 64));

        var extension = dump.AsSpan(extensionRecordNumber * recordSize, recordSize);
        BinaryPrimitives.WriteUInt64LittleEndian(extension[0x20..],
            ((ulong)options.BaseSequenceNumber << 48) | (options.ReferencedRecordNumber ?? (uint)baseRecordNumber));
        var attribute = extension[MftDumpFixture.FirstAttributeOffset(extension)..];
        attribute[..128].Clear();
        var length = options.Resident ? (24 + (int)options.Size + 7) & ~7 : options.Truncated ? 24 : 80;
        BinaryPrimitives.WriteUInt32LittleEndian(attribute, options.MissingData ? uint.MaxValue : 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[4..], (uint)length);
        attribute[8] = options.Resident ? (byte)0 : (byte)1;
        attribute[9] = options.Named ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(attribute[10..], 64);
        if (options.Resident)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(attribute[16..], (uint)options.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(attribute[20..], 24);
        }
        else if (!options.Truncated)
        {
            BinaryPrimitives.WriteInt64LittleEndian(attribute[16..], options.LowestVcn);
            BinaryPrimitives.WriteInt64LittleEndian(attribute[48..], options.Size);
        }

        if (options.Named)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(attribute[64..], 'x');
        }

        BinaryPrimitives.WriteUInt32LittleEndian(attribute[length..], options.MalformedTail ? 0x40u : uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(extension[0x18..],
            (uint)(MftDumpFixture.FirstAttributeOffset(extension) + length + 8));
        return dump;
    }

    static int EndOfAttributes(ReadOnlySpan<byte> record)
    {
        var offset = MftDumpFixture.FirstAttributeOffset(record);
        while (BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]) != uint.MaxValue)
        {
            offset += (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
        }

        return offset;
    }
}
