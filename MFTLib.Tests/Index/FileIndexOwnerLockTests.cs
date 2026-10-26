using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Two <see cref="FileIndex" /> instances over one cache directory: the second opener can
///     never validate, rename, sweep, or delete a block the first index owns. A cache-only
///     second open reports <see cref="DriveFailureKind.InUse" />; a non-cache-only second open
///     scans into a private block and leaves the canonical cache alone.
/// </summary>
[TestClass]
public sealed class FileIndexOwnerLockTests : IDisposable
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

    FileIndexOptions Options(bool cacheOnly = false, IProgress<IndexScanProgress>? progress = null,
        Action<string>? diagnostics = null) =>
        _fixture.Options(volumeSerial: _volumeSerial,
            cacheMode: cacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent) with
        {
            ScanProgress = progress,
            Diagnostics = diagnostics
        };

    string CanonicalPath => Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial));

    [TestMethod]
    public async Task OpenAsync_CacheOnly_SecondIndexReportsInUseAndLeavesTheFirstIndexsBlockAlone()
    {
        await using (var first = await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
            Assert.AreEqual(CacheLocation.OwnedCanonical, first.Drives.Single().Block!.CacheLocation);
            Assert.AreEqual(BlockOrigin.ProducedByScan, first.Drives.Single().Block!.Origin);
            Assert.IsTrue(File.Exists(CanonicalPath));

            await using (var second = await FileIndex.OpenAsync(Options(cacheOnly: true), CancellationToken.None))
            {
                var status = second.Drives.Single();
                Assert.AreEqual(DriveState.Failed, status.State);
                Assert.IsNull(status.Block);
                Assert.AreEqual(DriveFailureKind.InUse, status.Failure?.Kind);
                Assert.AreEqual(
                    "Drive T: cache block is in use by another FileIndex and CacheMode.ExistingOnly forbids a scan.",
                    status.Failure?.Message);
                Assert.IsTrue(File.Exists(CanonicalPath),
                    "the second opener must not delete the first index's live block");
                Assert.AreEqual(DriveState.Ready, first.Drives.Single().State);
            }

            Assert.IsTrue(File.Exists(CanonicalPath),
                "disposing the declined opener leaves the owner's block in place");
        }

        // The lock died with the first index, so a later open takes the slot and warm-starts.
        await using var third = await FileIndex.OpenAsync(Options(cacheOnly: true), CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, third.Drives.Single().State);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, third.Drives.Single().Block!.Origin);
        Assert.AreEqual(CacheLocation.OwnedCanonical, third.Drives.Single().Block!.CacheLocation);
    }

    [TestMethod]
    public async Task OpenAsync_NonCacheOnly_SecondIndexScansAPrivateBlockAndNeverTouchesTheCanonicalCache()
    {
        await using var first = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var canonicalBytes = await SharedFileReader.ReadAllBytesAsync(CanonicalPath);

        string secondBlockPath;
        await using (var second = await FileIndex.OpenAsync(Options(), CancellationToken.None))
        {
            var status = second.Drives.Single();
            Assert.AreEqual(DriveState.Ready, status.State);
            Assert.AreEqual(BlockOrigin.ProducedByScan, status.Block!.Origin);
            Assert.AreEqual(CacheLocation.PrivateFallback, status.Block.CacheLocation);
            Assert.IsTrue(second.TryGetDriveOrdinal('T', out var secondOrdinal));
            secondBlockPath = second.CurrentSnapshot[secondOrdinal].Block.Path;
            Assert.IsTrue(File.Exists(secondBlockPath));
            Assert.AreSequenceEqual(canonicalBytes, await SharedFileReader.ReadAllBytesAsync(CanonicalPath), "the owning index's canonical cache is not replaced, truncated, or deleted");
            Assert.IsNotNull(second.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken));
        }

        Assert.IsFalse(File.Exists(secondBlockPath),
            "the private block is delete-on-close, so disposing the second index removes it");
        Assert.AreEqual(DriveState.Ready, first.Drives.Single().State);
    }

    [TestMethod]
    public async Task RescanAsync_SecondIndexDuringBlockedRescan_ReportsInUseAndTheRestoredCanonicalWarmStartsTheNextOpen()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var progress = new BlockOnFirstArmedReport();
        uint rowsBefore;
        DateTime timestampBefore;

        await using (var first = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None))
        {
            rowsBefore = first.HeaderOf().RowCount;
            timestampBefore = first.Drives[0].Block!.ScanTimestamp;

            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
            progress.Armed = true;
            var rescan = first.RescanAsync(['T'], cancellationTokenSource.Token);
            await progress.Reported;

            // The rescan is blocked mid-scan with the canonical file renamed aside. The second
            // opener cannot take the owner lock, so it reports InUse instead of validating the
            // half-written replacement or sweeping the renamed-aside file.
            await using (var second = await FileIndex.OpenAsync(Options(cacheOnly: true), CancellationToken.None))
            {
                Assert.AreEqual(DriveState.Failed, second.Drives.Single().State);
                Assert.AreEqual(DriveFailureKind.InUse, second.Drives.Single().Failure?.Kind);
            }

            await cancellationTokenSource.CancelAsync();
            progress.Release();
            await AssertThrowsCancellation(() => rescan);

            Assert.IsTrue(File.Exists(CanonicalPath), "the canonical file must be restored");
            Assert.IsEmpty(Directory.EnumerateFiles(CacheDirectoryPath,
                CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-*"));
        }

        await using var third = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, third.Drives[0].State);
        Assert.AreEqual(rowsBefore, third.HeaderOf().RowCount);
        Assert.AreEqual(timestampBefore, third.Drives[0].Block!.ScanTimestamp);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, third.Drives[0].Block!.Origin);
    }

    [TestMethod]
    public async Task RescanAsync_Completed_SecondIndexKeepsItsPrivateBlockWhenTheFirstIndexIsDisposed()
    {
        var deletions = new List<string>();
        var first = await FileIndex.OpenAsync(Options(diagnostics: deletions.Add), CancellationToken.None);
        try
        {
            var oldSnapshot = first.CurrentSnapshot;

            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
            await first.RescanAsync(['T'], CancellationToken.None);

            var retiredPaths = Directory
                .EnumerateFiles(CacheDirectoryPath, CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-*")
                .ToList();
            Assert.HasCount(1, retiredPaths);

            string secondBlockPath;
            await using (var second = await FileIndex.OpenAsync(Options(), CancellationToken.None))
            {
                Assert.IsTrue(second.TryGetDriveOrdinal('T', out var secondOrdinal));
                secondBlockPath = second.CurrentSnapshot[secondOrdinal].Block.Path;
                Assert.Contains("mftlib-private-", secondBlockPath);
                Assert.IsTrue(File.Exists(retiredPaths[0]),
                    "the second opener must not sweep the first index's retired sibling");

                await first.DisposeAsync();

                Assert.IsTrue(File.Exists(secondBlockPath),
                    "the first index's disposal deletes its own retired file, never the second index's block");
                Assert.IsFalse(File.Exists(retiredPaths[0]));
                Assert.AreEqual(DriveState.Ready, second.Drives.Single().State);
                Assert.IsNotNull(second.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken));
            }

            Assert.Contains(entry => entry.Contains(".retired-") && entry.Contains("superseded"), deletions,
                "the owning index logged its retired file's delete at release");
            GC.KeepAlive(oldSnapshot);
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RescanAsync_PrivateBlockWhileCanonicalIsOwned_RescansPrivatelyAndTakesTheCanonicalSlotOnceItIsFree()
    {
        var first = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        try
        {
            var canonicalBytes = await SharedFileReader.ReadAllBytesAsync(CanonicalPath);

            await using var second = await FileIndex.OpenAsync(Options(), CancellationToken.None);
            var initialStatus = second.Drives.Single();
            Assert.AreEqual(CacheLocation.PrivateFallback, initialStatus.Block!.CacheLocation);
            Assert.IsTrue(second.TryGetDriveOrdinal('T', out var secondOrdinal));
            var firstPrivatePath = second.CurrentSnapshot[secondOrdinal].Block.Path;

            // While the first index owns the slot, the second index's rescan must not rename the
            // canonical file aside: it scans privately again.
            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second", TestContext.CancellationToken);
            await second.RescanAsync(['T'], CancellationToken.None);

            var secondPrivatePath = second.CurrentSnapshot[secondOrdinal].Block.Path;
            Assert.AreEqual(CacheLocation.PrivateFallback, second.Drives.Single().Block!.CacheLocation);
            Assert.AreNotEqual(firstPrivatePath, secondPrivatePath);
            Assert.AreSequenceEqual(canonicalBytes, await SharedFileReader.ReadAllBytesAsync(CanonicalPath), "the canonical cache still belongs to the first index, untouched by the rescan");
            Assert.IsEmpty(Directory.EnumerateFiles(CacheDirectoryPath,
                CacheDirectory.BlockFileName('T', _volumeSerial) + ".retired-*"));
            Assert.AreEqual(DriveState.Ready, first.Drives.Single().State);

            // Once the owner is gone, the second index's next rescan takes the slot: the leftover
            // canonical file is renamed aside under the now-held lock and the new block takes the
            // canonical name, so later opens warm-start it.
            await first.DisposeAsync();

            Assert.AreEqual(CacheLocation.PrivateFallback, second.Drives.Single().Block!.CacheLocation);

            await second.RescanAsync(['T'], CancellationToken.None);

            Assert.AreEqual(CacheLocation.OwnedCanonical, second.Drives.Single().Block!.CacheLocation);
            Assert.AreEqual(BlockOrigin.ProducedByScan, second.Drives.Single().Block!.Origin);
            Assert.AreEqual(CacheLocation.PrivateFallback, initialStatus.Block.CacheLocation);
            Assert.AreEqual(DriveState.Ready, second.Drives.Single().State);
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RescanAsync_InUseFailedDrive_ScansIntoAPrivateBlockWhileTheOwnerStaysLive()
    {
        await using var first = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        var canonicalBytes = await SharedFileReader.ReadAllBytesAsync(CanonicalPath);

        await using var second = await FileIndex.OpenAsync(Options(cacheOnly: true), CancellationToken.None);
        Assert.AreEqual(DriveFailureKind.InUse, second.Drives.Single().Failure?.Kind);
        Assert.IsNull(second.Drives.Single().Block);

        await second.RescanAsync(['T'], CancellationToken.None);

        var status = second.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.IsNull(status.Failure);
        Assert.AreEqual(CacheLocation.PrivateFallback, status.Block!.CacheLocation);
        Assert.AreSequenceEqual(canonicalBytes, await SharedFileReader.ReadAllBytesAsync(CanonicalPath));
    }

    [TestMethod]
    public async Task OpenAsync_DisabledCacheMode_CacheSlotStaysNullAfterRescan()
    {
        await using var index = await FileIndex.OpenAsync(
            Options() with { CacheMode = CacheMode.Disabled }, CancellationToken.None);
        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
        Assert.IsNull(index.Drives.Single().Block!.CacheLocation);
        Assert.AreEqual(BlockOrigin.ProducedByScan, index.Drives.Single().Block!.Origin);

        await index.RescanAsync(['T'], CancellationToken.None);

        Assert.IsNull(index.Drives.Single().Block!.CacheLocation);
        Assert.IsFalse(File.Exists(CanonicalPath));
    }

    [TestMethod]
    public async Task OpenAsync_Offline_HasNoBlock()
    {
        var options = Options() with
        {
            Drives = [new IndexedDrive('T', Path.Combine(TreeRoot, "missing"), _volumeSerial)]
        };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(DriveState.Offline, index.Drives.Single().State);
        Assert.IsNull(index.Drives.Single().Block);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDrive_PublishesCanonicalCacheLocation()
    {
        await using var index = await FileIndex.OpenAsync(Options(cacheOnly: true), CancellationToken.None);
        Assert.AreEqual(DriveState.Failed, index.Drives.Single().State);
        Assert.IsNull(index.Drives.Single().Block);

        await index.RescanAsync(['T'], CancellationToken.None);

        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
        Assert.AreEqual(CacheLocation.OwnedCanonical, index.Drives.Single().Block!.CacheLocation);
        Assert.AreEqual(BlockOrigin.ProducedByScan, index.Drives.Single().Block!.Origin);
    }

    [TestMethod]
    public async Task OpenAsync_ProducerFailure_HasNoBlock()
    {
        var options = Options() with
        {
            Drives = [new IndexedDrive('T', TreeRoot, _volumeSerial)],
            MftSource = new MftIndexSource((_, _) => throw new IOException("Synthetic production failure"))
        };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(DriveState.Failed, index.Drives.Single().State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, index.Drives.Single().Failure?.Kind);
        Assert.IsNull(index.Drives.Single().Block);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RescanAsync_PrivateTakeover_ChangesCacheSlotOnlyAfterPublication(bool cancel)
    {
        var first = await FileIndex.OpenAsync(Options(), CancellationToken.None);
        try
        {
            var progress = new BlockOnFirstArmedReport();
            await using var second = await FileIndex.OpenAsync(Options(progress: progress), CancellationToken.None);
            var before = second.Drives.Single();
            var rowCountBefore = second.HeaderOf().RowCount;
            Assert.AreEqual(CacheLocation.PrivateFallback, before.Block!.CacheLocation);
            await first.DisposeAsync();

            using var cancellation = new CancellationTokenSource();
            progress.Armed = true;
            var rescan = second.RescanAsync(['T'], cancellation.Token);
            try
            {
                await progress.Reported.WaitAsync(HangGuard * 3, TestContext.CancellationToken);
                Assert.AreEqual(CacheLocation.PrivateFallback, second.Drives.Single().Block!.CacheLocation);
                Assert.AreEqual(rowCountBefore, second.HeaderOf().RowCount);
            }
            finally
            {
                if (cancel)
                {
                    await cancellation.CancelAsync();
                }

                progress.Release();
                if (cancel)
                {
                    await AssertThrowsCancellation(() => rescan);
                }
                else
                {
                    await rescan;
                }
            }

            Assert.AreEqual(cancel ? CacheLocation.PrivateFallback : CacheLocation.OwnedCanonical,
                second.Drives.Single().Block!.CacheLocation);
            Assert.AreEqual(CacheLocation.PrivateFallback, before.Block.CacheLocation);
            Assert.AreEqual(DriveState.Ready, second.Drives.Single().State);
            Assert.IsNotNull(second.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken));

            if (cancel)
            {
                progress.Armed = false;
                await second.RescanAsync(['T'], CancellationToken.None);
                Assert.AreEqual(CacheLocation.OwnedCanonical, second.Drives.Single().Block!.CacheLocation);
            }
        }
        finally
        {
            await first.DisposeAsync();
        }
    }

    static async Task AssertThrowsCancellation(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Assert.Fail("Expected an OperationCanceledException (or a derived type such as TaskCanceledException).");
    }

    public TestContext TestContext { get; set; } = null!;
}
