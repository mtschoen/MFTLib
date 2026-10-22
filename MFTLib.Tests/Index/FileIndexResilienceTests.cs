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
public sealed partial class FileIndexResilienceTests : IDisposable
{
    IndexTreeFixture _fixture = null!;
    string TreeRoot => _fixture.TreeRoot;
    string CacheDirectoryPath => _fixture.CacheDirectory;
    uint _volumeSerial;

    [TestInitialize]
    public void Initialize()
    {
        _volumeSerial = TestVolumeSerial.GetNext();
        _fixture = new IndexTreeFixture();
    }

    public void Dispose() => _fixture.Dispose();

    FileIndexOptions Options(bool cacheDisabled = false, IProgress<IndexScanProgress>? progress = null,
        Action<string>? diagnostics = null) =>
        _fixture.Options(volumeSerial: _volumeSerial, cacheMode: cacheDisabled ? CacheMode.Disabled : CacheMode.Persistent) with
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

        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial));
        if (File.Exists(blockPath))
        {
            // Proves the partial block is not still mapped: File.Delete would succeed even on a
            // file BlockFile still has mapped, since BlockFile opens with FileShare.Delete. An
            // exclusive open with FileShare.None succeeds only if no handle - mapped or not -
            // remains open on the file at all.
            await using var exclusive = new FileStream(blockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, reopened.Drives[0].State);
        Assert.IsGreaterThanOrEqualTo(3u, reopened.HeaderOf().RowCount);
    }

    [TestMethod]
    public async Task OpenAsync_SecondDriveCancelledMidScan_UnwindsTheFirstDrivesAlreadyAddedBlock()
    {
        var secondTreeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(secondTreeRoot, "Documents"));
        await File.WriteAllTextAsync(Path.Combine(secondTreeRoot, "Documents", "readme2.md"), "hello2", TestContext.CancellationToken).WaitAsync(HangGuard * 3, TestContext.CancellationToken);

        // Drives settle concurrently, so nothing but the first drive's own settle can say it has
        // been adopted. It warm-starts from a cache a first open leaves behind. The open is
        // cancelled only once that drive has reported settling and the second drive's scan is
        // parked inside its first progress report, so the unwind always has an adopted block to
        // release and the second drive is always cancelled mid-scan.
        var firstDrive = new IndexedDrive('T', TreeRoot, 0x0BADF00D);
        var warmingOpen = await FileIndex.OpenAsync(
            new FileIndexOptions
            {
                Drives = [firstDrive with { Producer = ProducerKind.Enumeration }],
                CacheDirectory = CacheDirectoryPath
            }, CancellationToken.None).WaitAsync(HangGuard * 3, TestContext.CancellationToken);
        await warmingOpen.DisposeAsync().AsTask().WaitAsync(HangGuard * 3, TestContext.CancellationToken);

        using var cancellationTokenSource = new CancellationTokenSource();
        var firstSettled = new DriveSettledSignal('T');
        var secondParked = new ParkUntilCancelled('U', cancellationTokenSource.Token);
        var options = new FileIndexOptions
        {
            Drives = [firstDrive with { Producer = ProducerKind.Enumeration }, new IndexedDrive('U', secondTreeRoot, 0x0BADF00E, ProducerKind.Enumeration)],
            CacheDirectory = CacheDirectoryPath,
            DriveOpenProgress = firstSettled,
            ScanProgress = secondParked
        };

        try
        {
            await AssertThrowsCancellation(async () =>
            {
                var opening = FileIndex.OpenAsync(options, cancellationTokenSource.Token);
                await Task.WhenAll(firstSettled.Reported, secondParked.Parked).WaitAsync(HangGuard * 3, TestContext.CancellationToken);
                Assert.IsFalse(opening.IsCompleted, "the second drive is still mid-scan when the open is cancelled");
                await cancellationTokenSource.CancelAsync().WaitAsync(HangGuard * 3, TestContext.CancellationToken);
                await opening.WaitAsync(HangGuard * 3, TestContext.CancellationToken);
            });

            var firstBlockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', 0x0BADF00D));

            // If the first drive's already-open mapping were not unwound, this exclusive
            // reopen would fail with a sharing violation instead of succeeding.
            await using var exclusive = new FileStream(firstBlockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            // The second drive's partly written canonical file may remain (a canonical target is
            // not delete-on-close); it must never be adoptable, so a later open cold-scans it.
            var secondBlockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('U', 0x0BADF00E));
            if (File.Exists(secondBlockPath))
            {
                Assert.IsNull(BlockFile.Open(secondBlockPath, 0x0BADF00E, out _),
                    "a scan cancelled mid-write leaves nothing a later open could adopt");
            }
        }
        finally
        {
            Directory.Delete(secondTreeRoot, recursive: true);
        }
    }

    /// <summary>Completes <see cref="Reported" /> when the named drive reports that it settled.</summary>
    sealed class DriveSettledSignal(char driveLetter) : IProgress<IndexDriveOpened>
    {
        readonly TaskCompletionSource _reported = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reported => _reported.Task;

        public void Report(IndexDriveOpened value)
        {
            if (value.Drive == driveLetter)
            {
                _reported.TrySetResult();
            }
        }
    }

    /// <summary>
    ///     Holds the named drive's scan in its first progress report, after completing
    ///     <see cref="Parked" />, until the token is cancelled.
    /// </summary>
    sealed class ParkUntilCancelled(char driveLetter, CancellationToken cancellationToken)
        : IProgress<IndexScanProgress>
    {
        readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Parked => _parked.Task;

        public void Report(IndexScanProgress value)
        {
            if (char.ToUpperInvariant((value.Drive).Letter) != driveLetter)
            {
                return;
            }

            _parked.TrySetResult();
            if (!cancellationToken.WaitHandle.WaitOne(HangGuard * 3))
            {
                throw new TimeoutException("The open was never cancelled.");
            }
        }
    }

    [TestMethod]
    public async Task OpenAsync_DisabledCacheMode_CancelledMidScan_DeletesTheTempBlockImmediately()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var options = Options(cacheDisabled: true, progress: new CancelOnFirstReport(cancellationTokenSource));

        await AssertThrowsCancellation(() => FileIndex.OpenAsync(options, cancellationTokenSource.Token));

        Assert.IsEmpty(Directory.EnumerateFiles(Path.GetTempPath(),
            $"mftlib-cache-disabled-*-{CacheDirectory.BlockFileName('T', _volumeSerial)}"));
    }

    [TestMethod]
    public async Task RescanAsync_CancelledMidScan_LeavesThePreviousBlockUsable()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cancellationTokenSource) { Armed = false };
        await using var index = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None);
        var rowsBefore = index.HeaderOf().RowCount;
        var timestampBefore = index.Drives[0].Block!.ScanTimestamp;

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
        progress.Armed = true;

        await AssertThrowsCancellation(() => index.RescanAsync(['T'], cancellationTokenSource.Token));

        Assert.AreEqual(rowsBefore, index.HeaderOf().RowCount);
        Assert.AreEqual(timestampBefore, index.Drives[0].Block!.ScanTimestamp);
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
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
            timestampBefore = index.Drives[0].Block!.ScanTimestamp;

            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
            progress.Armed = true;

            await AssertThrowsCancellation(() => index.RescanAsync(['T'], cancellationTokenSource.Token));

            var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial));
            Assert.IsTrue(File.Exists(blockPath), "the canonical file must be restored, not left renamed aside");
            Assert.IsEmpty(Directory.EnumerateFiles(CacheDirectoryPath,
                CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-*"));
        }

        await using var reopened = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, reopened.Drives[0].State);
        Assert.AreEqual(rowsBefore, reopened.HeaderOf().RowCount);
        Assert.AreEqual(timestampBefore, reopened.Drives[0].Block!.ScanTimestamp);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Drives[0].Block!.Origin);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string GetCurrentBlockPath(FileIndex index, ushort driveOrdinal)
    {
        return index.CurrentSnapshot[driveOrdinal].Block.Path;
    }

    /// <summary>
    ///     The superseded snapshot is held by the test: only a held snapshot keeps its block mapped
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
            Assert.IsTrue(index.TryGetDriveOrdinal('T', out var driveOrdinal));
            var firstSnapshot = index.CurrentSnapshot;
            firstPath = firstSnapshot[driveOrdinal].Block.Path;
            Assert.IsTrue(File.Exists(firstPath));

            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
            await index.RescanAsync(['T'], CancellationToken.None);
            secondPath = GetCurrentBlockPath(index, driveOrdinal);

            Assert.AreNotEqual(firstPath, secondPath);
            Assert.IsTrue(File.Exists(firstPath), "a superseded temp block stays mapped while its snapshot is held");
            GC.KeepAlive(firstSnapshot);
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
            Assert.IsTrue(index.TryGetDriveOrdinal('T', out var driveOrdinal));
            var firstPath = GetCurrentBlockPath(index, driveOrdinal);
            Assert.IsTrue(File.Exists(firstPath));

            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
            await index.RescanAsync(['T'], CancellationToken.None);
            Assert.AreNotEqual(firstPath, GetCurrentBlockPath(index, driveOrdinal));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.IsFalse(File.Exists(firstPath), "nothing holds the superseded snapshot, so finalization releases it");
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
        Assert.IsTrue(index.TryGetDriveOrdinal('T', out var driveOrdinal));
        var oldSnapshot = index.CurrentSnapshot;
        var canonicalPath = oldSnapshot[driveOrdinal].Block.Path;

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
        await index.RescanAsync(['T'], CancellationToken.None);

        var newPath = index.CurrentSnapshot[driveOrdinal].Block.Path;
        Assert.AreEqual(canonicalPath, newPath, "the new block takes the same canonical cache path");

        var retiredPaths = Directory
            .EnumerateFiles(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-*")
            .ToList();
        Assert.HasCount(1, retiredPaths);
        Assert.IsTrue(File.Exists(retiredPaths[0]));

        await oldSnapshot.ReleaseNowAsync();

        Assert.IsFalse(File.Exists(retiredPaths[0]));
    }

    [TestMethod]
    public async Task RescanAsync_BackToBackRescans_ProduceDistinctRetiredNamesAndBothAreCleanedUpAfterRelease()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var firstSnapshot = index.CurrentSnapshot;

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
        await index.RescanAsync(['T'], CancellationToken.None);
        var secondSnapshot = index.CurrentSnapshot;

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "third.md"), "third", TestContext.CancellationToken);
        await index.RescanAsync(['T'], CancellationToken.None);

        var retiredPaths = Directory
            .EnumerateFiles(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-*")
            .ToList();
        Assert.HasCount(2, retiredPaths, "each rescan should retire a distinctly named file");
        Assert.AreNotEqual(retiredPaths[0], retiredPaths[1]);
        Assert.IsTrue(retiredPaths.All(File.Exists));
        Assert.HasCount(1, Directory.GetFiles(CacheDirectoryPath, "*.mlix"), "exactly one canonical block file remains");
        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);

        await firstSnapshot.ReleaseNowAsync();
        await secondSnapshot.ReleaseNowAsync();

        Assert.IsTrue(retiredPaths.All(path => !File.Exists(path)));
    }

    [TestMethod]
    public async Task ReadsAfterDispose_ThrowObjectDisposedException()
    {
        var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var entry = index.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken)!.Value;
        await index.DisposeAsync();

        Assert.ThrowsExactly<ObjectDisposedException>(() => index.Drives);
        Assert.ThrowsExactly<ObjectDisposedException>(() => index.CurrentSnapshot);
        Assert.ThrowsExactly<ObjectDisposedException>(() => index.TryGetDriveOrdinal('T', out _));
        Assert.ThrowsExactly<ObjectDisposedException>(index.BorrowCurrentSnapshot);
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
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, reopened.Drives[0].Block!.Origin);
    }

    [TestMethod]
    public async Task RescanAsync_CanonicalCacheFileMissing_StillSucceedsWithoutRenamingAnything()
    {
        await using var index = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var canonicalPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial));

        // Simulates a canonical cache file removed out from under the index between opens, so
        // RenameAsideForRescan finds nothing to rename aside. Safe while the block is still
        // mapped: BlockFile opens with FileShare.Delete.
        File.Delete(canonicalPath);

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
        await index.RescanAsync(['T'], CancellationToken.None);

        Assert.IsTrue(File.Exists(canonicalPath));
        Assert.IsEmpty(Directory.EnumerateFiles(CacheDirectoryPath,
            CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-*"));
    }

    [TestMethod]
    public async Task OpenAsync_DisabledCacheMode_AnotherTestInstanceDoesNotDeleteTheLiveTempBlock()
    {
        var firstFixture = new FileIndexResilienceTests();
        var secondFixture = new FileIndexResilienceTests();
        firstFixture.Initialize();
        secondFixture.Initialize();

        try
        {
            await using var firstIndex =
                await FileIndex.OpenAsync(firstFixture.Options(cacheDisabled: true), CancellationToken.None);
            Assert.IsTrue(firstIndex.TryGetDriveOrdinal('T', out var firstDriveOrdinal));
            var firstBlockPath = firstIndex.CurrentSnapshot[firstDriveOrdinal].Block.Path;
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
