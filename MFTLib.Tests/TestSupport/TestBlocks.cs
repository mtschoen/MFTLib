using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.TestSupport;

/// <summary>Creates unseeded blocks; the caller owns the returned mapping.</summary>
static class TestBlocks
{
    public static BlockFile Create(string? path = null,
        ProducerKind producer = ProducerKind.Enumeration,
        uint slotCapacity = 64, uint namePoolCapacity = 512, uint? nameEntryCapacity = null,
        uint volumeSerial = 0x0BADF00D, uint rootRow = 0,
        bool? deleteOnClose = null, CacheTag cacheTag = default, ulong scanShape = 0)
        => BlockFile.Create(Options(path, producer, slotCapacity, namePoolCapacity, nameEntryCapacity,
            volumeSerial, rootRow, deleteOnClose, cacheTag, scanShape));

    public static BlockFileCreateOptions Options(string? path = null,
        ProducerKind producer = ProducerKind.Enumeration,
        uint slotCapacity = 64, uint namePoolCapacity = 512, uint? nameEntryCapacity = null,
        uint volumeSerial = 0x0BADF00D, uint rootRow = 0,
        bool? deleteOnClose = null, CacheTag cacheTag = default, ulong scanShape = 0)
        => new()
        {
            Path = path ?? Path.Combine(Path.GetTempPath(), $"mftlib-block-{Guid.NewGuid():N}.mlix"),
            Root = SeededBlocks.RootFor(path),
            VolumeSerial = volumeSerial,
            ProducerKind = producer,
            SlotCapacity = slotCapacity,
            NameEntryCapacity = nameEntryCapacity ?? slotCapacity,
            NamePoolCapacity = namePoolCapacity,
            RootRow = rootRow,
            DeleteOnClose = deleteOnClose ?? (path is null),
            CacheTag = cacheTag,
            ScanShape = scanShape
        };

    public static string WriteCache(string directory, char letter,
        uint volumeSerial = 0x0BADF00D,
        ProducerKind producer = ProducerKind.Enumeration,
        Action<BlockFile>? mutate = null)
    {
        var path = Path.Combine(directory, CacheDirectory.BlockFileName(new VolumeKey(letter), volumeSerial));
        using var block = Create(path, producer, slotCapacity: 8, namePoolCapacity: 4096,
            volumeSerial: volumeSerial, rootRow: producer == ProducerKind.Mft ? 5u : 0u);
        var writer = new BlockWriter(block);
        var columns = new RowColumns(0, RowFlags.InUse, 0, 0, 0, 0);
        Assert.IsTrue(writer.TryWriteRow(0, directory, columns));
        if (producer == ProducerKind.Mft)
        {
            Assert.IsTrue(writer.TryWriteRow(5, "not-the-drive-root", columns));
        }
        writer.Complete(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);
        mutate?.Invoke(block);
        block.Flush(null);
        return path;
    }

    public static string WriteInvalidCache(string directory, char letter, uint volumeSerial = 1)
    {
        var path = Path.Combine(directory, CacheDirectory.BlockFileName(new VolumeKey(letter), volumeSerial));
        File.WriteAllText(path, "deliberately invalid block content");
        return path;
    }
}
