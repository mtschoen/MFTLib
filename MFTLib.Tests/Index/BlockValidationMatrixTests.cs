using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class BlockValidationMatrixTests : IndexTreeTestBase
{
    FileIndexOptions Options(uint volumeSerial = 0x0BADF00D) =>
        Fixture.Options(volumeSerial: volumeSerial);

    string BlockPath(uint volumeSerial = 0x0BADF00D)
    {
        return Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('T'), volumeSerial));
    }

    async Task SeedCacheAsync()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
    }

    /// <summary>
    ///     The open deletes the cached block for <paramref name="expectedRejection" />, which its
    ///     diagnostics name, and cold-scans the root.
    /// </summary>
    static async Task AssertColdScansAsync(FileIndexOptions options, BlockValidationResult expectedRejection)
    {
        var diagnostics = new List<string>();
        await using var index = await FileIndex.OpenAsync(options with { Diagnostics = diagnostics.Add },
            CancellationToken.None);
        Assert.Contains(line => line.Contains($"cache validation failed: {expectedRejection}"), diagnostics,
            $"the rejection reason {expectedRejection} is logged");
        Assert.AreEqual(RootState.Ready, index.Status.State);
        Assert.AreEqual(BlockOrigin.ProducedByScan, index.Status.Block!.Origin);
        Assert.IsGreaterThanOrEqualTo(3u, index.HeaderOf().RowCount);
    }

    [TestMethod]
    public async Task CorruptedMagic_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath(), TestContext.CancellationToken);
        bytes[0] = 0x00;
        await File.WriteAllBytesAsync(BlockPath(), bytes, TestContext.CancellationToken);

        await AssertColdScansAsync(Options(), BlockValidationResult.WrongMagic);
    }

    [TestMethod]
    public async Task WrongFormatVersion_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath(), TestContext.CancellationToken);
        BitConverter.GetBytes(BlockLayout.FormatVersion + 1).CopyTo(bytes, 4);
        await File.WriteAllBytesAsync(BlockPath(), bytes, TestContext.CancellationToken);

        await AssertColdScansAsync(Options(), BlockValidationResult.WrongFormatVersion);
    }

    [TestMethod]
    public async Task MissingCompleteFlag_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath(), TestContext.CancellationToken);
        BitConverter.GetBytes((uint)BlockFlags.None).CopyTo(bytes, 12);
        await File.WriteAllBytesAsync(BlockPath(), bytes, TestContext.CancellationToken);

        await AssertColdScansAsync(Options(), BlockValidationResult.Incomplete);
    }

    /// <summary>
    ///     Patches the volume serial field inside the block already sitting at its canonical
    ///     cache path (rather than opening with a differently configured serial, which would
    ///     point at a different cache file name and never exercise this rejection at all) and
    ///     reopens with the volume's original serial, so the header's serial no longer matches
    ///     what the caller expects.
    /// </summary>
    [TestMethod]
    public async Task WrongVolumeSerial_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath(), TestContext.CancellationToken);
        BitConverter.GetBytes(0x99999999u).CopyTo(bytes, 16);
        await File.WriteAllBytesAsync(BlockPath(), bytes, TestContext.CancellationToken);

        await AssertColdScansAsync(Options(), BlockValidationResult.WrongVolumeSerial);
    }

    [TestMethod]
    public async Task TruncatedBlock_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath(), TestContext.CancellationToken);
        await File.WriteAllBytesAsync(BlockPath(), [.. bytes.AsSpan(0, BlockLayout.PageSize)], TestContext.CancellationToken);

        await AssertColdScansAsync(Options(), BlockValidationResult.InconsistentRegions);
    }

    [TestMethod]
    public async Task NameDescriptorPastUsedPool_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath(), TestContext.CancellationToken);
        const long rowOneNameOffset = BlockLayout.RowRegionOffset + BlockLayout.RowBytes + 8;
        BitConverter.GetBytes(uint.MaxValue - 1).CopyTo(bytes, rowOneNameOffset);
        await File.WriteAllBytesAsync(BlockPath(), bytes, TestContext.CancellationToken);

        await AssertColdScansAsync(Options(), BlockValidationResult.InvalidNameDescriptor);
    }

    [TestMethod]
    public async Task SequenceRegionOffsetWrong_ColdScans()
    {
        await SeedCacheAsync();
        var bytes = await File.ReadAllBytesAsync(BlockPath(), TestContext.CancellationToken);
        BitConverter.GetBytes(123ul).CopyTo(bytes, 96);
        await File.WriteAllBytesAsync(BlockPath(), bytes, TestContext.CancellationToken);

        await AssertColdScansAsync(Options(), BlockValidationResult.InconsistentRegions);
    }

    // Resolves to the same reason as CorruptedMagic: BlockFile.Open rejects any file shorter
    // than the header page before it ever reads a magic value, so an empty file and a file with
    // a corrupted magic both surface as WrongMagic even though the underlying defect differs.
    [TestMethod]
    public async Task EmptyBlockFile_ColdScans()
    {
        await SeedCacheAsync();
        await File.WriteAllBytesAsync(BlockPath(), [], TestContext.CancellationToken);

        await AssertColdScansAsync(Options(), BlockValidationResult.WrongMagic);
    }

    [TestMethod]
    public async Task WrongRootDirectory_ColdScans()
    {
        await SeedCacheAsync();
        using var differentTreeRootOwner = new TempDirectory("mftlib-other");
        var differentTreeRoot = differentTreeRootOwner.Path;
        Directory.CreateDirectory(Path.Combine(differentTreeRoot, "Documents"));
        await File.WriteAllTextAsync(Path.Combine(differentTreeRoot, "Documents", "readme.md"), "hello", TestContext.CancellationToken);
        var options = new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), differentTreeRoot, 0x0BADF00D, ProducerKind.Enumeration),
            CacheDirectory = CacheDirectoryPath
        };

        await AssertColdScansAsync(options, BlockValidationResult.WrongRootDirectory);
    }

    public TestContext TestContext { get; set; } = null!;
}
