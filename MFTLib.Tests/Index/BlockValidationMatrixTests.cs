using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class BlockValidationMatrixTests
{
    OwnedIndexDirectories _directories = null!;
    string _treeRoot = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directories = new OwnedIndexDirectories();
        _treeRoot = _directories.TreeRoot;
        _cacheDirectory = _directories.CacheDirectory;
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _directories.Dispose();
    }

    FileIndexOptions Options(uint volumeSerial = 0x0BADF00D)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, volumeSerial)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration
        };
    }

    string BlockPath(uint volumeSerial = 0x0BADF00D)
    {
        return Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', volumeSerial));
    }

    async Task SeedCacheAsync()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
    }

    /// <summary>
    ///     The open deletes the cached block for <paramref name="expectedRejection" />, which its
    ///     diagnostics name, and cold-scans the drive.
    /// </summary>
    static async Task AssertColdScansAsync(FileIndexOptions options, BlockValidationResult expectedRejection)
    {
        var diagnostics = new List<string>();
        await using var index = await FileIndex.OpenAsync(options with { Diagnostics = diagnostics.Add },
            CancellationToken.None);
        Assert.IsTrue(diagnostics.Any(line => line.Contains($"cache validation failed: {expectedRejection}")),
            $"the rejection reason {expectedRejection} is logged");
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.AreEqual(BlockSource.ProducedByScan, index.Drives[0].Block.Source);
        Assert.IsTrue(index.HeaderOf().RowCount >= 3);
    }

    [TestMethod]
    public async Task CorruptedMagic_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath());
        bytes[0] = 0x00;
        await File.WriteAllBytesAsync(BlockPath(), bytes);

        await AssertColdScansAsync(Options(), BlockValidationResult.WrongMagic);
    }

    [TestMethod]
    public async Task WrongFormatVersion_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath());
        BitConverter.GetBytes(BlockLayout.FormatVersion + 1).CopyTo(bytes, 4);
        await File.WriteAllBytesAsync(BlockPath(), bytes);

        await AssertColdScansAsync(Options(), BlockValidationResult.WrongFormatVersion);
    }

    [TestMethod]
    public async Task MissingCompleteFlag_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath());
        BitConverter.GetBytes((uint)BlockFlags.None).CopyTo(bytes, 12);
        await File.WriteAllBytesAsync(BlockPath(), bytes);

        await AssertColdScansAsync(Options(), BlockValidationResult.Incomplete);
    }

    /// <summary>
    ///     Patches the volume serial field inside the block already sitting at its canonical
    ///     cache path (rather than opening with a differently configured serial, which would
    ///     point at a different cache file name and never exercise this rejection at all) and
    ///     reopens with the drive's original serial, so the header's serial no longer matches
    ///     what the caller expects.
    /// </summary>
    [TestMethod]
    public async Task WrongVolumeSerial_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath());
        BitConverter.GetBytes(0x99999999u).CopyTo(bytes, 16);
        await File.WriteAllBytesAsync(BlockPath(), bytes);

        await AssertColdScansAsync(Options(), BlockValidationResult.WrongVolumeSerial);
    }

    [TestMethod]
    public async Task TruncatedBlock_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath());
        await File.WriteAllBytesAsync(BlockPath(), bytes.AsSpan(0, BlockLayout.PageSize).ToArray());

        await AssertColdScansAsync(Options(), BlockValidationResult.InconsistentRegions);
    }

    [TestMethod]
    public async Task NameDescriptorPastUsedPool_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath());
        var rowOneNameOffset = BlockLayout.RowRegionOffset + BlockLayout.RowBytes + 8;
        BitConverter.GetBytes(uint.MaxValue - 1).CopyTo(bytes, rowOneNameOffset);
        await File.WriteAllBytesAsync(BlockPath(), bytes);

        await AssertColdScansAsync(Options(), BlockValidationResult.InvalidNameDescriptor);
    }

    [TestMethod]
    public async Task SequenceRegionOffsetWrong_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath());
        BitConverter.GetBytes(123ul).CopyTo(bytes, 96);
        await File.WriteAllBytesAsync(BlockPath(), bytes);

        await AssertColdScansAsync(Options(), BlockValidationResult.InconsistentRegions);
    }

    // Resolves to the same reason as CorruptedMagic: BlockFile.Open rejects any file shorter
    // than the header page before it ever reads a magic value, so an empty file and a file with
    // a corrupted magic both surface as WrongMagic even though the underlying defect differs.
    [TestMethod]
    public async Task EmptyBlockFile_ColdScans()
    {
        await SeedCacheAsync();
        await File.WriteAllBytesAsync(BlockPath(), []);

        await AssertColdScansAsync(Options(), BlockValidationResult.WrongMagic);
    }

    [TestMethod]
    public async Task WrongRootDirectory_ColdScans()
    {
        await SeedCacheAsync();
        var differentTreeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(differentTreeRoot, "Documents"));
        await File.WriteAllTextAsync(Path.Combine(differentTreeRoot, "Documents", "readme.md"), "hello");
        try
        {
            var options = new FileIndexOptions
            {
                Drives = [new IndexedDrive('T', differentTreeRoot, 0x0BADF00D)],
                CacheDirectory = _cacheDirectory,
                ProducerPolicy = ProducerPolicy.Enumeration
            };

            await AssertColdScansAsync(options, BlockValidationResult.WrongRootDirectory);
        }
        finally
        {
            if (Directory.Exists(differentTreeRoot))
            {
                Directory.Delete(differentTreeRoot, recursive: true);
            }
        }
    }
}
