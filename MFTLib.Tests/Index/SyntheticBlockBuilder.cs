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
        return AddRow(name, parentRow: 0, RowFlags.InUse | RowFlags.Directory, size: 0,
            modifiedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    public uint AddRow(string name, uint parentRow, RowFlags flags, long size, DateTime modifiedUtc,
        uint attributes = 0)
    {
        var rowIndex = _nextRow++;
        ref var header = ref _block.Header;
        var nameOffsetBytes = header.NamePoolUsed;
        var poolCharacterIndex = (int)(nameOffsetBytes / sizeof(char));
        name.AsSpan().CopyTo(_block.NamePoolCharacters.Slice(poolCharacterIndex, name.Length));
        header.NamePoolUsed = nameOffsetBytes + (uint)(name.Length * sizeof(char));

        ref var row = ref _block.Rows[(int)rowIndex];
        row = new FileRow
        {
            ParentRow = parentRow,
            Attributes = attributes,
            Size = size,
            ModifiedTicks = modifiedUtc.Ticks
        };

        // Goes through the descriptor-word helper for the same reason the production writer
        // does: the name offset, the name length, and the flags are one 64-bit value.
        FileRow.WriteDescriptorWord(ref row, nameOffsetBytes, (ushort)name.Length, flags);

        header.RowCount = rowIndex + 1;
        return rowIndex;
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
        header.ScanTimestampTicks = scanTimestampUtc.Ticks;
        header.Generation = 1;
        header.Flags |= BlockFlags.Complete;
        _block.Flush();
    }

    public BlockFile? OpenForReading(out BlockValidationResult validation)
    {
        return BlockFile.Open(BlockPath, VolumeSerial, out validation);
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
