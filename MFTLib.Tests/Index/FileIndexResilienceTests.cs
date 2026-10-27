using System.Runtime.CompilerServices;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Failure, cancellation, and cleanup behavior for <see cref="FileIndex" />: a scan that
///     throws or is cancelled must not leak a mapping or a temp file, a rescan that fails must
///     leave the previous block usable, and superseded blocks (disabled-cache temp files and
///     cache-mode rescan leftovers) must be cleaned up deterministically.
/// </summary>
[TestClass]
public sealed partial class FileIndexResilienceTests : IndexTreeTestBase
{
    FileIndexOptions Options(bool cacheDisabled = false, IProgress<IndexScanProgress>? progress = null,
        Action<string>? diagnostics = null) =>
        Fixture.Options(volumeSerial: UniqueVolumeSerial, cacheMode: cacheDisabled ? CacheMode.Disabled : CacheMode.Persistent) with
        {
            ScanProgress = progress,
            Diagnostics = diagnostics
        };

    /// <summary>
    ///     Cancels the token the moment the first directory finishes, so cancellation lands
    ///     mid-scan rather than before anything was created. Starts disarmed so the same
    ///     instance can ride through an initial scan unharmed and only interrupt a later one
    ///     (a rescan reuses <see cref="FileIndexOptions.ScanProgress" /> from the original open).
    /// </summary>
    sealed class CancelOnFirstReport(CancellationTokenSource cancellationTokenSource) : IProgress<IndexScanProgress>
    {
        public bool Armed { get; set; } = true;

        public void Report(IndexScanProgress value)
        {
            if (Armed)
            {
                cancellationTokenSource.Cancel();
            }
        }
    }

    static async Task AssertThrowsCancellation(Func<Task> action)
    {
        try
        {
            await action().WaitAsync(HangGuard * 3);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("Expected an OperationCanceledException (or a derived type such as TaskCanceledException).");
    }

    [TestMethod]
    public async Task OpenAsync_CancelledMidScan_LeavesTheBlockUnlockedAndTheNextOpenColdScansCleanly()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var options = Options(progress: new CancelOnFirstReport(cancellationTokenSource));

        await AssertThrowsCancellation(() => FileIndex.OpenAsync(options, cancellationTokenSource.Token));

        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('T'), UniqueVolumeSerial));
        if (File.Exists(blockPath))
        {
            // Proves the partial block is not still mapped: File.Delete would succeed even on a
            // file BlockFile still has mapped, since BlockFile opens with FileShare.Delete. An
            // exclusive open with FileShare.None succeeds only if no handle - mapped or not -
            // remains open on the file at all.
            await using var exclusive = new FileStream(blockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, reopened.Status.State);
        Assert.IsGreaterThanOrEqualTo(3u, reopened.HeaderOf().RowCount);
    }

    [TestMethod]
    public async Task OpenAsync_DisabledCacheMode_CancelledMidScan_DeletesTheTempBlockImmediately()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var options = Options(cacheDisabled: true, progress: new CancelOnFirstReport(cancellationTokenSource));

        await AssertThrowsCancellation(() => FileIndex.OpenAsync(options, cancellationTokenSource.Token));

        Assert.IsEmpty(Directory.EnumerateFiles(Directory.GetParent(TreeRoot)!.FullName,
            $"mftlib-cache-disabled-*-{CacheDirectory.BlockFileName(new VolumeKey('T'), UniqueVolumeSerial)}"));
    }

    [TestMethod]
    public async Task RescanAsync_CancelledMidScan_LeavesThePreviousBlockUsable()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cancellationTokenSource) { Armed = false };
        await using var index = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None);
        var rowsBefore = index.HeaderOf().RowCount;
        var timestampBefore = index.Status.Block!.ScanTimestamp;

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
        progress.Armed = true;

        await AssertThrowsCancellation(() => index.RescanAsync(cancellationTokenSource.Token));

        Assert.AreEqual(rowsBefore, index.HeaderOf().RowCount);
        Assert.AreEqual(timestampBefore, index.Status.Block!.ScanTimestamp);
        Assert.AreEqual(DriveState.Ready, index.Status.State);
    }

    [TestMethod]
    public async Task RescanAsync_CancelledMidScan_RestoresTheCanonicalFileSoTheNextOpenWarmStarts()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cancellationTokenSource) { Armed = false };
        uint rowsBefore;
        DateTime timestampBefore;

        await using (var index = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None))
        {
            rowsBefore = index.HeaderOf().RowCount;
            timestampBefore = index.Status.Block!.ScanTimestamp;

            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
            progress.Armed = true;

            await AssertThrowsCancellation(() => index.RescanAsync(cancellationTokenSource.Token));

            var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('T'), UniqueVolumeSerial));
            Assert.IsTrue(File.Exists(blockPath), "the canonical file must be restored, not left renamed aside");
            Assert.IsEmpty(Directory.EnumerateFiles(CacheDirectoryPath,
                CacheDirectory.BlockFileName(new VolumeKey('T'), UniqueVolumeSerial) + ".retired-*"));
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, reopened.Status.State);
        Assert.AreEqual(rowsBefore, reopened.HeaderOf().RowCount);
        Assert.AreEqual(timestampBefore, reopened.Status.Block!.ScanTimestamp);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Status.Block!.Origin);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string GetCurrentBlockPath(FileIndex index)
    {
        return index.CurrentBlock!.Block.Path;
    }

    /// <summary>
    ///     The superseded block is held by the test: only a held block keeps its file mapped
    ///     (see docs/query-lifetime.md), and an unheld one is released by its finalizer whenever a
    ///     collection happens to run, which parallel test classes make frequent.
    /// </summary>
    [TestMethod]
    public async Task DisposeAsync_DisabledCacheMode_DeletesEveryTempBlockIncludingAHeldSupersededOne()
    {
        var index = await FileIndex.OpenAsync(Options(cacheDisabled: true), CancellationToken.None);
        string firstPath;
        string secondPath;
        try
        {
            var firstBlock = index.CurrentBlock!;
            firstPath = firstBlock.Block.Path;
            Assert.IsTrue(File.Exists(firstPath));

            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
            await index.RescanAsync(CancellationToken.None);
            secondPath = GetCurrentBlockPath(index);

            Assert.AreNotEqual(firstPath, secondPath);
            Assert.IsTrue(File.Exists(firstPath), "a superseded temp block stays mapped while its block is held");
            GC.KeepAlive(firstBlock);
        }
        finally
        {
            await index.DisposeAsync();
        }

        Assert.IsFalse(File.Exists(firstPath));
        Assert.IsFalse(File.Exists(secondPath));
    }

    [TestMethod]
    public async Task RescanAsync_DisabledCacheMode_UnheldSupersededTempBlockIsDeletedByFinalization()
    {
        var index = await FileIndex.OpenAsync(Options(cacheDisabled: true), CancellationToken.None);
        try
        {
            var firstPath = GetCurrentBlockPath(index);
            Assert.IsTrue(File.Exists(firstPath));

            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
            await index.RescanAsync(CancellationToken.None);
            Assert.AreNotEqual(firstPath, GetCurrentBlockPath(index));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.IsFalse(File.Exists(firstPath), "nothing holds the superseded block, so finalization releases it");
        }
        finally
        {
            await index.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RescanAsync_CacheMode_RetiredFileExistsWhileHeldAndIsGoneAfterRelease()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var oldBlock = index.CurrentBlock!;
        var canonicalPath = oldBlock.Block.Path;

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
        await index.RescanAsync(CancellationToken.None);

        var newPath = index.CurrentBlock!.Block.Path;
        Assert.AreEqual(canonicalPath, newPath, "the new block takes the same canonical cache path");

        var retiredPaths = Directory
            .EnumerateFiles(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('T'), UniqueVolumeSerial) + ".retired-*")
            .ToList();
        Assert.HasCount(1, retiredPaths);
        Assert.IsTrue(File.Exists(retiredPaths[0]));

        await oldBlock.ReleaseNowAsync();

        Assert.IsFalse(File.Exists(retiredPaths[0]));
    }

    [TestMethod]
    public async Task RescanAsync_BackToBackRescans_ProduceDistinctRetiredNamesAndBothAreCleanedUpAfterRelease()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var firstBlock = index.CurrentBlock!;

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
        await index.RescanAsync(CancellationToken.None);
        var secondBlock = index.CurrentBlock!;

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "third.md"), "third", TestContext.CancellationToken);
        await index.RescanAsync(CancellationToken.None);

        var retiredPaths = Directory
            .EnumerateFiles(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('T'), UniqueVolumeSerial) + ".retired-*")
            .ToList();
        Assert.HasCount(2, retiredPaths, "each rescan should retire a distinctly named file");
        Assert.AreNotEqual(retiredPaths[0], retiredPaths[1]);
        Assert.IsTrue(retiredPaths.All(File.Exists));
        Assert.HasCount(1, Directory.GetFiles(CacheDirectoryPath, "*.mlix"), "exactly one canonical block file remains");
        Assert.AreEqual(DriveState.Ready, index.Status.State);

        await firstBlock.ReleaseNowAsync();
        await secondBlock.ReleaseNowAsync();

        Assert.IsTrue(retiredPaths.All(path => !File.Exists(path)));
    }

    [TestMethod]
    public async Task ReadsAfterDispose_ThrowObjectDisposedException()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var entry = index.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken)!.Value;
        await index.DisposeAsync();

        Assert.ThrowsExactly<ObjectDisposedException>(() => index.Status);
        Assert.ThrowsExactly<ObjectDisposedException>(() => index.CurrentBlock);
        Assert.ThrowsExactly<ObjectDisposedException>(() => { using var _ = index.BorrowCurrentSnapshot(); });
        Assert.IsTrue(entry.IsDisposed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = entry.Name);
    }

    [TestMethod]
    public async Task OpenAsync_WarmStart_LeavesDiscardedBlockNull()
    {
        await using (await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Status.Block!.Origin);
    }

    [TestMethod]
    public async Task RescanAsync_CanonicalCacheFileMissing_StillSucceedsWithoutRenamingAnything()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var canonicalPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('T'), UniqueVolumeSerial));

        // Simulates a canonical cache file removed out from under the index between opens, so
        // RenameAsideForRescan finds nothing to rename aside. Safe while the block is still
        // mapped: BlockFile opens with FileShare.Delete.
        File.Delete(canonicalPath);

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
        await index.RescanAsync(CancellationToken.None);

        Assert.IsTrue(File.Exists(canonicalPath));
        Assert.IsEmpty(Directory.EnumerateFiles(CacheDirectoryPath,
            CacheDirectory.BlockFileName(new VolumeKey('T'), UniqueVolumeSerial) + ".retired-*"));
    }

    [TestMethod]
    public async Task OpenAsync_DisabledCacheMode_AnotherTestInstanceDoesNotDeleteTheLiveTempBlock()
    {
        var firstFixture = new FileIndexResilienceTests();
        var secondFixture = new FileIndexResilienceTests();
        firstFixture.InitializeIndexTree();
        secondFixture.InitializeIndexTree();

        try
        {
            await using var firstIndex =
                await FileIndex.OpenAsync(firstFixture.Options(cacheDisabled: true), CancellationToken.None);
            var firstBlockPath = firstIndex.CurrentBlock!.Block.Path;
            Assert.IsTrue(File.Exists(firstBlockPath));

            await using var secondIndex =
                await FileIndex.OpenAsync(secondFixture.Options(cacheDisabled: true), CancellationToken.None);

            Assert.IsTrue(File.Exists(firstBlockPath),
                "opening another test fixture must not unlink this fixture's live disabled-cache block");
        }
        finally
        {
            firstFixture.Dispose();
            secondFixture.Dispose();
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
