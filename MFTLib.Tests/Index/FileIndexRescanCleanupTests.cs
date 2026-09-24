using System.Reflection;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The rescan failure paths around the swap gate and the renamed-aside cache file: a
///     commit cancelled at the swap gate restores the canonical file, a restore that cannot
///     delete a locked replacement stays best-effort, a blockless drive's cancelled adoption
///     leaves it blockless, and reclaiming a session whose pump recorded a subscriber fault
///     swallows that fault so the rescan can start a fresh session.
/// </summary>
[TestClass]
public class FileIndexRescanCleanupTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    string _treeRoot = null!;
    string _cacheDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_treeRoot);
        Directory.CreateDirectory(_cacheDirectory);
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

    [TestMethod]
    public async Task RescanAsync_CancelledAtTheSwapGate_RestoresTheRenamedAsideCacheFile()
    {
        var producedBlocks = new List<BlockFile>();
        var producerReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = (request, _cancellationToken) =>
            {
                WriteBlock(request.BlockPath, request.VolumeSerial, journalId: 7, nextUsn: 4096);
                var block = BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!;
                producedBlocks.Add(block);
                if (producedBlocks.Count == 2)
                {
                    producerReturned.TrySetResult();
                }

                return Task.FromResult(new MftBlockProduceResult(block,
                    JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false));
            }
        };
        await using var index = await FileIndex.OpenAsync(options, Token);
        var canonicalPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 1));
        Assert.IsTrue(File.Exists(canonicalPath), "the cold scan wrote the canonical cache file");

        var swapGate = SwapGateOf(index);
        await swapGate.WaitAsync(Token);
        try
        {
            using var rescanCancellation = new CancellationTokenSource();
            var rescan = index.RescanAsync('T', rescanCancellation.Token);

            // The rescan has produced its replacement block and is parked at the swap gate.
            await producerReturned.Task.WaitAsync(FakeIndexWatchSource.HangGuard);
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
            swapGate.Release();
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
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = (request, _cancellationToken) =>
            {
                if (failTheScan.Value)
                {
                    File.WriteAllBytes(request.BlockPath, [1, 2, 3]);
                    lockedReplacement = new FileStream(request.BlockPath, FileMode.Open, FileAccess.Read,
                        FileShare.None);
                    throw producerFailure;
                }

                WriteBlock(request.BlockPath, request.VolumeSerial, journalId: 7, nextUsn: 4096);
                return Task.FromResult(new MftBlockProduceResult(
                    BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
                    JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false));
            }
        };
        await using var index = await FileIndex.OpenAsync(options, Token);
        var canonicalPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 1));
        Assert.IsTrue(File.Exists(canonicalPath));

        failTheScan.Value = true;
        await index.RescanAsync('T', Token);

        try
        {
            var drive = index.Drives.Single();
            Assert.AreEqual("the producer lost the volume", drive.MftProducerFailureMessage);
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
    public async Task RescanAsync_CacheDeclinedDriveCancelledAtTheSwapGate_StaysBlockless()
    {
        var producerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerMayReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            InitialOpenCacheOnly = true,
            MftProducer = async (request, _cancellationToken) =>
            {
                producerEntered.TrySetResult();
                await producerMayReturn.Task;
                WriteBlock(request.BlockPath, request.VolumeSerial, journalId: 7, nextUsn: 4096);
                return new MftBlockProduceResult(
                    BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
                    JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false);
            }
        };
        await using var index = await FileIndex.OpenAsync(options, Token);
        var declined = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, declined.State);
        Assert.AreEqual(DriveFailureKind.CacheDeclined, declined.FailureKind);

        var swapGate = SwapGateOf(index);
        await swapGate.WaitAsync(Token);
        try
        {
            using var rescanCancellation = new CancellationTokenSource();
            var rescan = index.RescanAsync('T', rescanCancellation.Token);

            await producerEntered.Task.WaitAsync(FakeIndexWatchSource.HangGuard);
            // Cancel before the producer returns: the scan completes normally, and the
            // cancellation is observed by the commit's swap-gate wait.
            await rescanCancellation.CancelAsync();
            producerMayReturn.TrySetResult();

            try
            {
                await rescan;
                Assert.Fail("Expected an OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
                // Expected: the commit's swap-gate wait observes the cancellation. The
                // concrete subtype differs between a pre-cancelled and a parked wait.
            }

            var drive = index.Drives.Single();
            Assert.AreEqual(DriveState.Failed, drive.State);
            Assert.AreEqual(DriveFailureKind.CacheDeclined, drive.FailureKind,
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
            swapGate.Release();
        }
    }

    [TestMethod]
    public async Task RescanAsync_WhenTheEndedSessionRecordedASubscriberFault_CarriesItIntoAFreshSession()
    {
        using var harness = new WatchHarness();
        var subscriberFault = new InvalidOperationException("the subscriber blew up");
        harness.Index.Changed += _ => throw subscriberFault;
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        // The subscriber fault is recorded and the pump keeps going; when the source then
        // ends, the pump completes with the fault latched for the next stop.
        await harness.PublishAsync(WatchHarness.Batch('T', 9, "fresh.txt"));
        await harness.CompleteSourceAsync();
        await harness.SourceEndedAsync();
        await harness.WaitForPumpToCompleteAsync();

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        // The reclaimed session's pump launches via Task.Yield() (FileIndex.WatchPump.PumpAsync),
        // deliberately releasing the state lock before the fresh source connects, so RescanAsync
        // returning does not itself guarantee the second StartWatching call has run yet. Wait for
        // that fresh session to actually start before reading the invocation count it bumps.
        await harness.SourceStartedAsync();

        Assert.AreEqual(2, harness.SourceInvocationCount,
            "the rescan reclaimed the ended session and started a fresh one");
        Assert.AreEqual(DriveState.Ready, harness.Index.Drives.Single().State);

        // The rescan recovers its drive, not the subscriber, so the stop still reports it.
        var thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => harness.Index.StopWatchingAsync(Token));
        Assert.AreSame(subscriberFault, thrown);
    }

    /// <summary>
    ///     The reclaim path's stop has nothing to rethrow when the pump ended with no fault:
    ///     the watch's caller token was cancelled, ending the watch quietly while leaving the
    ///     session claimed, and the rescan's stop of it returns normally.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_WhenTheWatchsCallerTokenEndedTheSession_ReclaimsItWithoutAFault()
    {
        using var harness = new WatchHarness();
        using var watchCancellation = new CancellationTokenSource();
        await harness.Index.StartWatchingAsync(watchCancellation.Token);
        await harness.SourceStartedAsync();

        await watchCancellation.CancelAsync();
        await harness.SourceEndedAsync();
        await harness.WaitForPumpToCompleteAsync();

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        Assert.AreEqual(DriveState.Ready, harness.Index.Drives.Single().State);
        Assert.IsNull(harness.Index.Drives.Single().WatchFailureMessage);
    }

    [TestMethod]
    public async Task RescanAsync_AfterTheSourceEndedWithoutAStop_ClearsTheStaleFaultedCatchUp()
    {
        using var harness = new WatchHarness();
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Index.WatchFaulted += _ => announced.TrySetResult();
        await harness.Index.StartWatchingAsync(Token);
        await harness.SourceStartedAsync();

        // The source ending without a stop faults every watched drive's catch-up and
        // releases the session, leaving the faulted slot behind.
        await harness.CompleteSourceAsync();
        await announced.Task.WaitAsync(FakeIndexWatchSource.HangGuard);
        Assert.AreEqual(WatchCatchUpState.Faulted, harness.Index.Drives.Single().WatchCatchUp);
        Assert.IsNotNull(harness.Index.Drives.Single().WatchFailureMessage);

        harness.SetNextProducedCursor('T', journalId: 13, nextUsn: 9000);
        await harness.Index.RescanAsync('T', Token);

        var drive = harness.Index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(WatchCatchUpState.NotStarted, drive.WatchCatchUp,
            "the rescan removes the stale faulted slot with no session to arm onto");
        Assert.IsNull(drive.WatchFailureMessage);
    }

    static SemaphoreSlim SwapGateOf(FileIndex index)
    {
        var swapGateField = typeof(FileIndex).GetField("_swapGate",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (SemaphoreSlim)swapGateField.GetValue(index)!;
    }

    static void WriteBlock(string path, uint volumeSerial, ulong journalId, long nextUsn)
    {
        var moment = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
        var createOptions = new BlockFileCreateOptions
        {
            Path = path,
            VolumeSerial = volumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(8),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(256)
        };
        using (var block = BlockFile.Create(createOptions))
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "$MFT",
                new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0,
                    ModifiedTicks: moment.Ticks, SequenceNumber: 0));
            writer.TryWriteRow(5, ".",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0, Size: 0,
                    ModifiedTicks: moment.Ticks, SequenceNumber: 0));
            writer.SetJournalCursor(journalId, nextUsn);
            writer.Complete(moment);
        }
    }
}
