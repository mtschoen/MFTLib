using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Every block-file delete a <see cref="FileIndex" /> performs is reported through
///     <see cref="FileIndexOptions.Diagnostics" /> with the deleted path and the reason, and a
///     cache-only open declines a drive whose cache was rejected.
/// </summary>
public partial class FileIndexResilienceTests
{
    [TestMethod]
    public async Task OpenAsync_CacheOnly_WithACorruptCache_DeclinesTheDriveWithNoBlock()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', UniqueVolumeSerial));
        var bytes = await File.ReadAllBytesAsync(blockPath, TestContext.CancellationToken);
        bytes[0] = 0xFF;
        await File.WriteAllBytesAsync(blockPath, bytes, TestContext.CancellationToken);

        var cacheOnlyOptions = Options();
        cacheOnlyOptions = cacheOnlyOptions with { CacheMode = CacheMode.ExistingOnly };
        await using var index = await FileIndex.OpenAsync(cacheOnlyOptions, CancellationToken.None);

        var failed = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(DriveFailureKind.CacheDeclined, failed.Failure?.Kind);
        Assert.IsNull(failed.Block);
        Assert.AreSequenceEqual(bytes, await File.ReadAllBytesAsync(blockPath, TestContext.CancellationToken), "a cache-only open leaves the rejected block in place");
    }

    [TestMethod]
    public async Task OpenAsync_WithDiagnostics_LogsTheStaleRetiredSiblingSweep()
    {
        Directory.CreateDirectory(CacheDirectoryPath);
        var staleRetiredPath = Path.Combine(CacheDirectoryPath,
            CacheDirectory.BlockFileName('T', UniqueVolumeSerial) + ".retired-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(staleRetiredPath, "leftover from a killed process", TestContext.CancellationToken);

        var deletions = new List<string>();
        await using var index = await FileIndex.OpenAsync(Options(diagnostics: deletions.Add),
            CancellationToken.None);

        Assert.IsFalse(File.Exists(staleRetiredPath));
        var line = deletions.SingleOrDefault(entry => entry.Contains(staleRetiredPath));
        Assert.IsNotNull(line, "the sweep delete must be logged with its path");
        Assert.Contains("retired", line);
    }

    [TestMethod]
    public async Task OpenAsync_WithDiagnostics_LogsTheInvalidCacheDiscardWithItsValidationReason()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', UniqueVolumeSerial));
        var bytes = await File.ReadAllBytesAsync(blockPath, TestContext.CancellationToken);
        bytes[0] = 0xFF;
        await File.WriteAllBytesAsync(blockPath, bytes, TestContext.CancellationToken);

        var deletions = new List<string>();
        await using var index = await FileIndex.OpenAsync(Options(diagnostics: deletions.Add),
            CancellationToken.None);

        var line = deletions.SingleOrDefault(entry => entry.Contains(blockPath));
        Assert.IsNotNull(line, "the rejected cache delete must be logged with its path");
        Assert.Contains(nameof(BlockValidationResult.WrongMagic), line);
    }

    [TestMethod]
    public async Task OpenAsync_WithDiagnostics_LogsTheWrongRootDirectoryDiscard()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        var secondRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(secondRoot);
        try
        {
            var deletions = new List<string>();
            var options = new FileIndexOptions
            {
                Drives = [new IndexedDrive('T', secondRoot, UniqueVolumeSerial, ProducerKind.Enumeration)],
                CacheDirectory = CacheDirectoryPath,
                Diagnostics = deletions.Add
            };
            await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

            var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', UniqueVolumeSerial));
            Assert.AreEqual(BlockOrigin.ProducedByScan, index.Drives[0].Block!.Origin);
            var line = deletions.SingleOrDefault(entry => entry.Contains(blockPath));
            Assert.IsNotNull(line, "the wrong-root discard must be logged with its path");
            Assert.Contains(nameof(BlockValidationResult.WrongRootDirectory), line);
        }
        finally
        {
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task RescanAsync_WithDiagnostics_LogsTheSupersededBlocksDeleteAtRelease()
    {
        var deletions = new List<string>();
        await using var index = await FileIndex.OpenAsync(Options(diagnostics: deletions.Add),
            CancellationToken.None);
        var oldSnapshot = index.CurrentSnapshot;

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
        await index.RescanAsync(['T'], CancellationToken.None);

        await oldSnapshot.ReleaseNowAsync();

        var line = deletions.SingleOrDefault(entry => entry.Contains(".retired-"));
        Assert.IsNotNull(line, "the superseded block's delete at release must be logged with its path");
        Assert.Contains("superseded", line);
    }

    [TestMethod]
    public async Task RescanAsync_CancelledMidScan_WithDiagnostics_LogsThePartialReplacementDelete()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cancellationTokenSource) { Armed = false };
        var deletions = new List<string>();
        await using var index = await FileIndex.OpenAsync(
            Options(progress: progress, diagnostics: deletions.Add), CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
        progress.Armed = true;

        await AssertThrowsCancellation(() => index.RescanAsync(['T'], cancellationTokenSource.Token));

        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', UniqueVolumeSerial));
        var line = deletions.SingleOrDefault(entry => entry.Contains(blockPath) && entry.Contains("partial"));
        Assert.IsNotNull(line,
            "the delete of the cancelled scan's partial replacement must be logged with its path");
    }
}
