using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class BlockFileTests
{
    static readonly DateTime ScanMoment = new(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Open_BlockWithoutCompleteFlag_IsRejected()
    {
        using var builder = new SyntheticBlockBuilder();
        builder.AddRoot();

        using var block = BlockFile.Open(builder.BlockPath, builder.VolumeSerial, out var validation);
        Assert.AreEqual(BlockValidationResult.Incomplete, validation);
        Assert.IsNull(block);
    }

    [TestMethod]
    public void CompletedBlock_OpensAndRoundTripsRowsAndNames()
    {
        using var builder = new SyntheticBlockBuilder();
        var root = builder.AddRoot();
        var childRow = builder.AddRow("report.pdf", root, RowFlags.InUse, size: 4096,
            modifiedUtc: ScanMoment, attributes: 32);
        builder.Complete(ScanMoment);

        using var block = builder.OpenForReading(out var validation);
        Assert.AreEqual(BlockValidationResult.Valid, validation);
        Assert.IsNotNull(block);

        Assert.AreEqual(BlockLayout.Magic, block.Header.Magic);
        Assert.AreEqual(BlockLayout.FormatVersion, block.Header.FormatVersion);
        Assert.AreEqual(ProducerKind.Enumeration, block.Header.ProducerKind);
        Assert.AreEqual(2u, block.Header.RowCount);
        Assert.AreEqual((ulong)BlockLayout.RowRegionOffset, block.Header.RowRegionOffset);
        Assert.AreEqual((ulong)BlockLayout.NamePoolOffset(block.Header.SlotCapacity), block.Header.NamePoolOffset);
        Assert.AreEqual(ScanMoment, block.Header.ScanTimestampUtc);

        var row = block.Rows[(int)childRow];
        Assert.AreEqual(root, row.ParentRow);
        Assert.AreEqual(4096L, row.Size);
        Assert.AreEqual(32u, row.Attributes);
        Assert.IsTrue(row.IsInUse);
        Assert.IsFalse(row.IsDirectory);

        var name = block.NamePoolCharacters.Slice((int)(row.NameOffsetBytes / sizeof(char)), row.NameLengthUnits);
        Assert.AreEqual("report.pdf", new string(name));
    }

    [TestMethod]
    public void Open_WrongVolumeSerial_IsRejected()
    {
        using var builder = new SyntheticBlockBuilder();
        builder.AddRoot();
        builder.Complete(ScanMoment);

        using var block = BlockFile.Open(builder.BlockPath, builder.VolumeSerial + 1, out var validation);
        Assert.AreEqual(BlockValidationResult.WrongVolumeSerial, validation);
        Assert.IsNull(block);
    }

    [TestMethod]
    public void Open_WrongMagic_IsRejected()
    {
        using var builder = new SyntheticBlockBuilder();
        builder.AddRoot();
        builder.Complete(ScanMoment);
        builder.MutateHeader((ref header) => header.Magic = 0xFFFFFFFF);

        using var block = builder.OpenForReading(out var validation);
        Assert.AreEqual(BlockValidationResult.WrongMagic, validation);
        Assert.IsNull(block);
    }

    [TestMethod]
    public void Open_WrongFormatVersion_IsRejected()
    {
        using var builder = new SyntheticBlockBuilder();
        builder.AddRoot();
        builder.Complete(ScanMoment);
        builder.MutateHeader((ref header) => header.FormatVersion = BlockLayout.FormatVersion + 7);

        using var block = builder.OpenForReading(out var validation);
        Assert.AreEqual(BlockValidationResult.WrongFormatVersion, validation);
        Assert.IsNull(block);
    }

    [TestMethod]
    public void Open_MissingFile_IsReportedWithoutThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"mftlib-missing-{Guid.NewGuid():N}.mlix");
        using var block = BlockFile.Open(missing, 1, out var validation);
        Assert.AreEqual(BlockValidationResult.WrongMagic, validation);
        Assert.IsNull(block);
    }

    [TestMethod]
    public void Open_TruncatedFile_IsRejectedWithoutThrowing()
    {
        using var builder = new SyntheticBlockBuilder();
        builder.AddRoot();
        builder.Complete(ScanMoment);
        var truncated = Path.Combine(builder.DirectoryPath, "truncated.mlix");
        var headerPageBytes = new byte[BlockLayout.PageSize];
        using (var reader = new FileStream(builder.BlockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            reader.ReadExactly(headerPageBytes);
        }

        File.WriteAllBytes(truncated, headerPageBytes);

        using var block = BlockFile.Open(truncated, builder.VolumeSerial, out var validation);
        Assert.AreEqual(BlockValidationResult.InconsistentRegions, validation);
        Assert.IsNull(block);
    }

    [TestMethod]
    public void DeleteOnClose_RemovesTheFileWhenDisposed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mftlib-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "volatile.mlix");
        try
        {
            using (BlockFile.Create(new BlockFileCreateOptions
            {
                Path = path,
                VolumeSerial = 1,
                ProducerKind = ProducerKind.Enumeration,
                SlotCapacity = 64,
                NamePoolCapacity = 256,
                DeleteOnClose = true
            }))
            {
                Assert.IsTrue(File.Exists(path));
            }

            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Rows_LengthIsSlotCapacityNotRowCount()
    {
        using var builder = new SyntheticBlockBuilder(slotCapacity: 256);
        builder.AddRoot();
        builder.Complete(ScanMoment);

        using var block = builder.OpenForReading(out _);
        Assert.AreEqual(256, block!.Rows.Length);
    }

    [TestMethod]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        using var builder = new SyntheticBlockBuilder();
        builder.AddRoot();
        builder.Complete(ScanMoment);

        var block = builder.OpenForReading(out _)!;
        block.Dispose();
        block.Dispose();
    }

    [TestMethod]
    public void Length_IsTheTotalMappedByteLength()
    {
        using var builder = new SyntheticBlockBuilder(slotCapacity: 64, namePoolCapacity: 512);
        builder.AddRoot();
        builder.Complete(ScanMoment);

        using var block = builder.OpenForReading(out _);
        var expectedLength = BlockLayout.TotalBlockBytes(64, 512);
        Assert.AreEqual(expectedLength, block!.Length);
    }

    [TestMethod]
    public void OpenMapping_MappedFileConstructionItselfFails_DisposesTheFileStreamBeforeRethrowing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mftlib-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "empty.mlix");
        File.WriteAllBytes(path, []);
        try
        {
            // An existing but empty file with a zero mapping capacity makes
            // MemoryMappedFile.CreateFromFile itself throw, before it is ever assigned, which
            // exercises the branch that disposes the FileStream directly rather than through it.
            Assert.ThrowsException<ArgumentException>(() =>
                BlockFile.OpenMapping(path, FileMode.Open, mappingCapacity: 0, viewLength: 0));

            // If the failed attempt's FileStream were not disposed, this exclusive reopen would
            // fail with a sharing violation instead of succeeding.
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void OpenMapping_MappingConstructionFails_ReleasesTheFileHandleBeforeRethrowing()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mftlib-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "unmappable.mlix");
        try
        {
            // A view larger than the mapping's own capacity makes CreateViewAccessor throw after
            // the FileStream and MemoryMappedFile already exist, exercising the cleanup path.
            Assert.ThrowsException<UnauthorizedAccessException>(() =>
                BlockFile.OpenMapping(path, FileMode.Create, mappingCapacity: 4096, viewLength: 8192));

            // If the failed attempt's FileStream were not disposed, this exclusive reopen would
            // fail with a sharing violation instead of succeeding.
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void BuildAndInitialize_ConstructionFails_DisposesTheMappingAndDeletesTheFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mftlib-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "partial.mlix");
        try
        {
            var options = new BlockFileCreateOptions
            {
                Path = path,
                VolumeSerial = 0x0BADF00D,
                ProducerKind = ProducerKind.Enumeration,
                SlotCapacity = 64,
                NamePoolCapacity = 4096
            };
            var length = BlockLayout.TotalBlockBytes(options.SlotCapacity, options.NamePoolCapacity);
            var (mappedFile, view) = BlockFile.OpenMapping(path, FileMode.Create, length, length);

            // Disposing the view before handing it over makes acquiring the base pointer in the
            // constructor throw, reproducing the state a real failure leaves behind: the mapping
            // exists and the file is on disk, but no BlockFile owns either of them yet.
            view.Dispose();

            Assert.ThrowsException<ObjectDisposedException>(
                () => BlockFile.BuildAndInitialize(options, length, mappedFile, view));

            // Cache mode does not delete on close, so nothing else would ever remove this file:
            // it would occupy the canonical path carrying no valid header at all.
            Assert.IsFalse(File.Exists(path), "a failed create must not leave a header-less file behind");

            // A surviving mapping would keep the deleted file alive and fail this outright.
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(directory).Count());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
