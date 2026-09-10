using MFTLib.Index;

namespace MFTLib.Tests.Index;

public delegate void HeaderMutation(ref BlockHeader header);

/// <summary>
///     Builds a small, complete block in a fresh temp directory by writing rows and names
///     directly through the mapped spans. Deliberately independent of the production
///     BlockWriter and enumeration producer so a failure points at one component.
/// </summary>
internal sealed class SyntheticBlockBuilder : IDisposable
{
    readonly BlockFile _block;
    uint _nextRow;

    public SyntheticBlockBuilder(char driveLetter = 'T', uint volumeSerial = 0x0BADF00D,
        uint slotCapacity = 256, uint namePoolCapacity = 4096)
    {
        DriveLetter = driveLetter;
        VolumeSerial = volumeSerial;
        DirectoryPath = Path.Combine(Path.GetTempPath(), $"mftlib-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
        BlockPath = Path.Combine(DirectoryPath, $"{driveLetter}-{volumeSerial:X8}.mlix");

        _block = BlockFile.Create(new BlockFileCreateOptions
        {
            Path = BlockPath,
            VolumeSerial = volumeSerial,
            ProducerKind = ProducerKind.Enumeration,
            SlotCapacity = slotCapacity,
            NamePoolCapacity = namePoolCapacity
        });
    }

    public char DriveLetter { get; }

    public uint VolumeSerial { get; }

    public string DirectoryPath { get; }

    public string BlockPath { get; }

    /// <summary>Adds the volume root at row 0. The root's parent is itself, per the format.</summary>
    public uint AddRoot(string name = "")
    {
        return AddRow(name, new RowColumns(ParentRow: 0, Flags: RowFlags.InUse | RowFlags.Directory,
            Attributes: 0, Size: 0, ModifiedTicks: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
            SequenceNumber: 0));
    }

    public uint AddRow(string name, in RowColumns columns)
    {
        return AddRowAt(_nextRow, name, in columns);
    }

    public uint AddRow(string name, uint parentRow, RowFlags flags, long size, DateTime modifiedUtc,
        ushort sequenceNumber)
    {
        return AddRow(name, new RowColumns(parentRow, flags, 0, size, modifiedUtc.Ticks, sequenceNumber));
    }

    public uint AddRowAt(uint rowIndex, string name, in RowColumns columns)
    {
        if (_nextRow <= rowIndex)
        {
            _nextRow = rowIndex + 1;
        }

        ref var header = ref _block.Header;
        var nameOffsetBytes = header.NamePoolUsed;
        var poolCharacterIndex = (int)(nameOffsetBytes / sizeof(char));
        name.AsSpan().CopyTo(_block.NamePoolCharacters.Slice(poolCharacterIndex, name.Length));
        header.NamePoolUsed = nameOffsetBytes + (uint)(name.Length * sizeof(char));

        ref var row = ref _block.Rows[(int)rowIndex];
        row = new FileRow
        {
            ParentRow = columns.ParentRow,
            Attributes = columns.Attributes,
            Size = columns.Size,
            ModifiedTicks = columns.ModifiedTicks
        };

        // Goes through the descriptor-word helper for the same reason the production writer
        // does: the name offset, the name length, and the flags are one 64-bit value.
        FileRow.WriteDescriptorWord(ref row, nameOffsetBytes, (ushort)name.Length, columns.Flags);

        if (header.RowCount <= rowIndex)
        {
            header.RowCount = rowIndex + 1;
        }

        return rowIndex;
    }

    /// <summary>Sets the sequence number column for a row, independent of its descriptor word.</summary>
    public void SetSequenceNumber(uint rowIndex, ushort sequenceNumber)
    {
        _block.SequenceNumbers[(int)rowIndex] = sequenceNumber;
        _block.Flush();
    }

    public void MutateNameDescriptor(uint rowIndex, uint nameOffsetBytes, ushort nameLengthUnits)
    {
        ref var row = ref _block.Rows[(int)rowIndex];
        var descriptor = FileRow.ReadDescriptorWord(in row);
        FileRow.WriteDescriptorWord(ref row, nameOffsetBytes, nameLengthUnits,
            FileRow.DescriptorFlags(descriptor));
        _block.Flush();
    }

    public static SyntheticBlockBuilder MftShaped()
    {
        var moment = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
        var builder = new SyntheticBlockBuilder();
        builder.MutateHeader((ref header) =>
        {
            header.ProducerKind = ProducerKind.Mft;
            header.RootRow = 5;
        });

        builder.AddRowAt(0, "$MFT", new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0, ModifiedTicks: moment.Ticks, SequenceNumber: 0));
        builder.AddRowAt(5, ".", new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0, Size: 0, ModifiedTicks: moment.Ticks, SequenceNumber: 0));
        builder.AddRowAt(6, "documents", new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0, Size: 0, ModifiedTicks: moment.Ticks, SequenceNumber: 0));
        builder.AddRowAt(7, "notes.txt", new RowColumns(ParentRow: 6, Flags: RowFlags.InUse, Attributes: 0, Size: 99, ModifiedTicks: moment.Ticks, SequenceNumber: 0));

        builder.Complete(moment);
        return builder;
    }

    public void MutateHeader(HeaderMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation(ref _block.Header);
        _block.Flush();
    }

    public void Complete(DateTime scanTimestampUtc)
    {
        ref var header = ref _block.Header;
        var liveRowCount = 0u;
        for (var rowIndex = 0u; rowIndex < header.RowCount; rowIndex++)
        {
            var flags = FileRow.DescriptorFlags(
                FileRow.ReadDescriptorWord(in _block.Rows[(int)rowIndex]));
            if ((flags & RowFlags.InUse) != 0 && (flags & RowFlags.Tombstone) == 0)
            {
                liveRowCount++;
            }
        }

        header.LiveRowCount = liveRowCount;
        header.ScanTimestampTicks = scanTimestampUtc.Ticks;
        header.Generation = 1;
        header.Flags |= BlockFlags.Complete;
        _block.Flush();
    }

    public BlockFile? OpenForReading(out BlockValidationResult validation)
    {
        return BlockFile.Open(BlockPath, VolumeSerial, out validation);
    }

    public BlockFile OpenForWriting()
    {
        return _block;
    }

    public void Dispose()
    {
        _block.Dispose();
        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (IOException)
        {
            // A mapping a test still holds open keeps the file locked on Windows. The temp
            // directory is disposable either way, so a failed cleanup is not a test failure.
        }
    }
}
