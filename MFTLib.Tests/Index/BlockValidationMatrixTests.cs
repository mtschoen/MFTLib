using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class BlockValidationMatrixTests
{
    string _treeRoot = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var directory in new[] { _treeRoot, _cacheDirectory })
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
    }

    FileIndexOptions Options(uint volumeSerial = 0x0BADF00D)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, volumeSerial)],
            CacheDirectory = _cacheDirectory
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

    static async Task AssertColdScansAsync(FileIndexOptions options, BlockValidationResult expectedDiscardReason)
    {
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.IsTrue(index.Drives[0].RowCount >= 3);
        Assert.AreEqual(expectedDiscardReason, index.Drives[0].DiscardedBlock);
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

    // Resolves to the same reason as CorruptedMagic: BlockFile.Open rejects any file shorter
    // than the header page before it ever reads a magic value, so an empty file and a file with
    // a corrupted magic both surface as WrongMagic even though the underlying defect differs.
    [TestMethod]
    public async Task EmptyBlockFile_ColdScans()
    {
        await SeedCacheAsync();
        File.WriteAllBytes(BlockPath(), []);

        await AssertColdScansAsync(Options(), BlockValidationResult.WrongMagic);
    }

    [TestMethod]
    public async Task WrongRootDirectory_ColdScans()
    {
        await SeedCacheAsync();
        var differentTreeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(differentTreeRoot, "Documents"));
        File.WriteAllText(Path.Combine(differentTreeRoot, "Documents", "readme.md"), "hello");
        try
        {
            var options = new FileIndexOptions
            {
                Drives = [new IndexedDrive('T', differentTreeRoot, 0x0BADF00D)],
                CacheDirectory = _cacheDirectory
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
