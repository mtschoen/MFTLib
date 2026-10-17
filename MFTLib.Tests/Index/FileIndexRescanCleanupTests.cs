using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The rescan failure paths around the drive's write gate and the renamed-aside cache file: a
///     commit cancelled at the write gate restores the canonical file, a restore that cannot
///     delete a locked replacement stays best-effort, a blockless drive's cancelled adoption
///     leaves it blockless.
/// </summary>
[TestClass]
public sealed class FileIndexRescanCleanupTests : IDisposable
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    IndexTreeFixture _fixture = null!;
    string _treeRoot => _fixture.TreeRoot;
    string _cacheDirectory => _fixture.CacheDirectory;

    [TestInitialize]
    public void Initialize()
    {
        _fixture = new IndexTreeFixture(seedDocuments: false);
        Directory.CreateDirectory(_cacheDirectory);
    }

    public void Dispose() => _fixture.Dispose();

    [TestMethod]
    public async Task RescanAsync_CancelledAtTheWriteGate_RestoresTheRenamedAsideCacheFile()
    {
        var producedBlocks = new List<BlockFile>();
        var producerReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory,
            MftSource = new MftIndexSource((request, _cancellationToken) =>
            {
                SeededBlocks.Write(request.BlockPath, request.VolumeSerial, journalIdentifier: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment);
                var block = BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!;
                producedBlocks.Add(block);
                if (producedBlocks.Count == 2)
                {
                    producerReturned.TrySetResult();
                }

                return Task.FromResult(new MftBlockProduceResult(block,
                    SkippedRecordCount: 0));
            })
        };
        await using var index = await FileIndex.OpenAsync(options, Token);
        var canonicalPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 1));
        Assert.IsTrue(File.Exists(canonicalPath), "the cold scan wrote the canonical cache file");

        await index.WaitForDriveWriteGateForTest('T');
        try
        {
            using var rescanCancellation = new CancellationTokenSource();
            var rescan = index.RescanAsync(['T'], rescanCancellation.Token);

            // The rescan has produced its replacement block and is parked at the write gate.
            await producerReturned.Task.WaitAsync(HangGuard);
            await rescanCancellation.CancelAsync();

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => rescan);

            var produced = producedBlocks[1];
            Assert.ThrowsException<ObjectDisposedException>(() => _ = produced.Header,
                "the unpublished replacement block is disposed by the failed commit");
            Assert.IsTrue(File.Exists(canonicalPath), "the renamed-aside file was moved back");
            Assert.AreEqual(0, Directory.GetFiles(_cacheDirectory, "*.retired-*").Length,
                "no retired sibling is left behind");
            Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
        }
        finally
        {
            index.ReleaseDriveWriteGateForTest('T');
        }
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")] // FileShare.None only blocks a delete on Windows
    public async Task RescanAsync_FailedScanWithALockedReplacementFile_RecordsTheFailureAndLeavesTheRetiredFileAside()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive(
                "Unix has no mandatory share-mode locking, so RestoreRetiredFile's delete of the " +
                "\"locked\" replacement succeeds there instead of failing the way this test requires.");
            return;
        }

        // The failed rescan's replacement file stays locked by this test, so the restore's
        // delete of it fails: the restore is best-effort and must not mask the real failure.
        var producerFailure = new IOException("the producer lost the volume");
        var failTheScan = new System.Runtime.CompilerServices.StrongBox<bool>(false);
        FileStream? lockedReplacement = null;
        var options = new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory,
            MftSource = new MftIndexSource((request, _cancellationToken) =>
            {
                if (failTheScan.Value)
                {
                    File.WriteAllBytes(request.BlockPath, [1, 2, 3]);
                    lockedReplacement = new FileStream(request.BlockPath, FileMode.Open, FileAccess.Read,
                        FileShare.None);
                    throw producerFailure;
                }

                SeededBlocks.Write(request.BlockPath, request.VolumeSerial, journalIdentifier: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment);
                return Task.FromResult(new MftBlockProduceResult(
                    BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
                    SkippedRecordCount: 0));
            })
        };
        await using var index = await FileIndex.OpenAsync(options, Token);
        var canonicalPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 1));
        Assert.IsTrue(File.Exists(canonicalPath));

        failTheScan.Value = true;
        var failure = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.RescanAsync(['T'], Token));

        try
        {
            Assert.AreSame(producerFailure, failure.InnerException);
            var drive = index.Drives.Single();
            Assert.AreEqual("the producer lost the volume", drive.Failure?.Message);
            Assert.AreEqual(DriveState.Ready, drive.State,
                "the drive keeps its previous block when its rescan's producer fails");
            Assert.AreEqual(1, Directory.GetFiles(_cacheDirectory, "*.retired-*").Length,
                "the restore could not move the renamed-aside file back over the locked replacement");
        }
        finally
        {
            lockedReplacement?.Dispose();
        }
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveCancelledAtTheWriteGate_StaysBlockless()
    {
        var producerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerMayReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory,
            CacheMode = CacheMode.ExistingOnly,
            MftSource = new MftIndexSource(async (request, _cancellationToken) =>
            {
                producerEntered.TrySetResult();
                await producerMayReturn.Task;
                SeededBlocks.Write(request.BlockPath, request.VolumeSerial, journalIdentifier: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment);
                return new MftBlockProduceResult(
                    BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
                    SkippedRecordCount: 0);
            })
        };
        await using var index = await FileIndex.OpenAsync(options, Token);
        var declined = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, declined.State);
        Assert.AreEqual(DriveFailureKind.CacheDeclined, declined.Failure?.Kind);

        await index.WaitForDriveWriteGateForTest('T');
        try
        {
            using var rescanCancellation = new CancellationTokenSource();
            var rescan = index.RescanAsync(['T'], rescanCancellation.Token);

            await producerEntered.Task.WaitAsync(HangGuard);
            // Cancel before the producer returns: the scan completes normally, and the
            // cancellation is observed by the commit's write-gate wait.
            await rescanCancellation.CancelAsync();
            producerMayReturn.TrySetResult();

            await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => rescan);

            var drive = index.Drives.Single();
            Assert.AreEqual(DriveState.Failed, drive.State);
            Assert.AreEqual(DriveFailureKind.CacheDeclined, drive.Failure?.Kind,
                "the rolled-back adoption leaves the drive blockless");
        }
        finally
        {
            // Safety net for a failure before the try block's own release (a WaitAsync
            // timeout, a thrown CancelAsync, or a failed assertion): TrySetResult is
            // idempotent, so this is a no-op on the normal path where line 205 already
            // released it. Without this, a stranded producer leaves the rescan permanently
            // awaiting it, and DisposeAsync then hangs waiting on that rescan instead of the
            // test reporting the real failure.
            producerMayReturn.TrySetResult();
            index.ReleaseDriveWriteGateForTest('T');
        }
    }

    /// <summary>
    ///     A drive whose watch ended without a stop reads faulted; a rescan replaces its block and,
    ///     since the watch is still requested, restarts it from the fresh cursor, which clears the
    ///     faulted catch-up and the failure message.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_AfterTheSourceEndedWithoutAStop_ClearsTheStaleFaultedCatchUp()
    {
        using var harness = new WatchHarness();
        await harness.Index.StartWatchingAsync(['T'], Token);
        harness.Source.WatchFor('T').End();
        await harness.WaitForFaultAsync(WatchFaultKind.Channel, 'T');
        Assert.AreEqual(WatchState.Faulted, harness.DriveFor('T').Watch.State);
        Assert.IsNotNull(harness.DriveFor('T').Watch.FailureMessage);

        harness.SetNextProducedCursor('T', journalIdentifier: 13, nextUsn: 9000);
        await harness.Index.RescanAsync(['T'], Token);

        var drive = harness.DriveFor('T');
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(WatchState.CatchingUp, drive.Watch.State,
            "the rescan replaced the faulted watch with one started from the fresh cursor");
        Assert.IsNull(drive.Watch.FailureMessage);
        Assert.AreEqual(new IndexWatchTarget('T', 13, 9000), harness.Source.TargetsFor('T')[^1]);
    }
}
