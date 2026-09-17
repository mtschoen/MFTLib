using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Every block-file delete a <see cref="FileIndex" /> performs is reported through
///     <see cref="FileIndexOptions.Diagnostics" /> with the deleted path and the reason, and a
///     failed drive keeps the <see cref="DriveStatus.DiscardedBlock" /> reason its cache was
///     rejected with.
/// </summary>
public partial class FileIndexResilienceTests
{
    [TestMethod]
    public async Task OpenAsync_CacheOnly_WithACorruptCache_KeepsTheDiscardedBlockReasonOnTheFailedDrive()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));
        var bytes = await File.ReadAllBytesAsync(blockPath);
        bytes[0] = 0xFF;
        await File.WriteAllBytesAsync(blockPath, bytes);

        var cacheOnlyOptions = Options();
        cacheOnlyOptions = cacheOnlyOptions with { InitialOpenCacheOnly = true };
        await using var index = await FileIndex.OpenAsync(cacheOnlyOptions, CancellationToken.None);

        var failed = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(DriveFailureKind.CacheDeclined, failed.FailureKind);
        Assert.AreEqual(BlockValidationResult.WrongMagic, failed.DiscardedBlock,
            "a failed drive keeps the reason its cache block was discarded");
    }

    [TestMethod]
    public async Task OpenAsync_WithDiagnostics_LogsTheStaleRetiredSiblingSweep()
    {
        Directory.CreateDirectory(_cacheDirectory);
        var staleRetiredPath = Path.Combine(_cacheDirectory,
            CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(staleRetiredPath, "leftover from a killed process");

        var deletions = new List<string>();
        await using var index = await FileIndex.OpenAsync(Options(diagnostics: deletions.Add),
            CancellationToken.None);

        Assert.IsFalse(File.Exists(staleRetiredPath));
        var line = deletions.SingleOrDefault(entry => entry.Contains(staleRetiredPath));
        Assert.IsNotNull(line, "the sweep delete must be logged with its path");
        StringAssert.Contains(line, "retired");
    }

    [TestMethod]
    public async Task OpenAsync_WithDiagnostics_LogsTheInvalidCacheDiscardWithItsValidationReason()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));
        var bytes = await File.ReadAllBytesAsync(blockPath);
        bytes[0] = 0xFF;
        await File.WriteAllBytesAsync(blockPath, bytes);

        var deletions = new List<string>();
        await using var index = await FileIndex.OpenAsync(Options(diagnostics: deletions.Add),
            CancellationToken.None);

        var line = deletions.SingleOrDefault(entry => entry.Contains(blockPath));
        Assert.IsNotNull(line, "the rejected cache delete must be logged with its path");
        StringAssert.Contains(line, nameof(BlockValidationResult.WrongMagic));
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
                Drives = [new IndexedDrive('T', secondRoot, _volumeSerial)],
                CacheDirectory = _cacheDirectory,
                ProducerPolicy = ProducerPolicy.Enumeration,
                Diagnostics = deletions.Add
            };
            await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

            var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));
            Assert.AreEqual(BlockValidationResult.WrongRootDirectory, index.Drives[0].DiscardedBlock);
            var line = deletions.SingleOrDefault(entry => entry.Contains(blockPath));
            Assert.IsNotNull(line, "the wrong-root discard must be logged with its path");
            StringAssert.Contains(line, nameof(BlockValidationResult.WrongRootDirectory));
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

        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        await index.RescanAsync('T', CancellationToken.None);

        await oldSnapshot.ReleaseNowAsync();

        var line = deletions.SingleOrDefault(entry => entry.Contains(".retired-"));
        Assert.IsNotNull(line, "the superseded block's delete at release must be logged with its path");
        StringAssert.Contains(line, "superseded");
    }

    [TestMethod]
    public async Task RescanAsync_CancelledMidScan_WithDiagnostics_LogsThePartialReplacementDelete()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cancellationTokenSource) { Armed = false };
        var deletions = new List<string>();
        await using var index = await FileIndex.OpenAsync(
            Options(progress: progress, diagnostics: deletions.Add), CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "second.md"), "second");
        progress.Armed = true;

        await AssertThrowsCancellation(() => index.RescanAsync('T', cancellationTokenSource.Token));

        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', _volumeSerial));
        var line = deletions.SingleOrDefault(entry => entry.Contains(blockPath) && entry.Contains("partial"));
        Assert.IsNotNull(line,
            "the delete of the cancelled scan's partial replacement must be logged with its path");
    }
}
