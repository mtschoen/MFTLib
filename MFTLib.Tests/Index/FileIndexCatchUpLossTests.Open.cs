using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.Index.FileIndexWatchRescanTests;

namespace MFTLib.Tests.Index;

/// <summary>
///     The open's settle runs the same scan operation: a drive whose catch-up is lost at open is
///     scanned again at once, and after the limit it settles <see cref="DriveState.Ready" /> with its
///     last block, unresumable, while <see cref="FileIndex.OpenAsync" /> does not throw. Each lost block
///     a retry supersedes leaves no file behind.
/// </summary>
public partial class FileIndexCatchUpLossTests
{
    [TestMethod]
    public async Task Open_CatchUpLostOnce_RetriesAndSettlesReady()
    {
        using var cache = new LossScriptedCache();
        cache.Losses.Enqueue(Loss('T'));

        await using var index = await FileIndex.OpenAsync(cache.Options(), Token);

        Assert.AreEqual(2, cache.Productions);
        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(BlockSource.ProducedByScan, drive.BlockSource);
        Assert.AreEqual(0, drive.ConsecutiveLostCatchUps);
        Assert.AreEqual(Loss('T'), drive.CheckpointLoss, "the retry keeps the report the loss produced");
        Assert.AreEqual(WatchCatchUpState.NotStarted, drive.WatchCatchUp);
        CollectionAssert.AreEqual(new[] { LossScriptedCache.CanonicalBlockName }, cache.BlockFileNames());
        await index.StartWatchingAsync('T', Token);
        Assert.AreEqual(1, cache.Source.StartsFor('T').Count, "the retried block is resumable");
    }

    [TestMethod]
    public async Task OpenAsync_ThreeLostCatchUps_SettlesReadyAndRefusesTheWatch()
    {
        using var cache = new LossScriptedCache();
        cache.Losses.Enqueue(Loss('T'));
        cache.Losses.Enqueue(Loss('T'));
        cache.Losses.Enqueue(Loss('T'));

        await using var index = await FileIndex.OpenAsync(cache.Options(), Token);

        Assert.AreEqual(3, cache.Productions);
        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State);
        Assert.AreEqual(3, drive.ConsecutiveLostCatchUps);
        Assert.AreEqual(Loss('T'), drive.CheckpointLoss);
        Assert.AreEqual(WatchCatchUpState.Faulted, drive.WatchCatchUp);
        StringAssert.Contains(drive.WatchFailureMessage, "12288");
        CollectionAssert.AreEqual(new[] { LossScriptedCache.CanonicalBlockName }, cache.BlockFileNames());
        var refusal = await ThrowsAsync<InvalidOperationException>(() => index.StartWatchingAsync('T', Token));
        StringAssert.Contains(refusal.Message, "RescanAsync");
    }

    [TestMethod]
    public async Task Open_CatchUpLostThenTheRetryScanFails_SettlesReadyWithTheLostBlockAndTheFailure()
    {
        using var cache = new LossScriptedCache();
        cache.Losses.Enqueue(Loss('T'));
        cache.FailProductionNumber = 2;

        await using var index = await FileIndex.OpenAsync(cache.Options(), Token);

        Assert.AreEqual(2, cache.Productions);
        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, drive.State, "the lost block stays in place");
        Assert.AreEqual(1, drive.ConsecutiveLostCatchUps);
        Assert.AreEqual(Loss('T'), drive.CheckpointLoss);
        Assert.AreEqual("the retry scan failed", drive.MftProducerFailureMessage);
        CollectionAssert.AreEqual(new[] { LossScriptedCache.CanonicalBlockName }, cache.BlockFileNames());
        var refusal = await ThrowsAsync<InvalidOperationException>(() => index.StartWatchingAsync('T', Token));
        StringAssert.Contains(refusal.Message, "RescanAsync");
    }

    /// <summary>
    ///     One MFT drive over an owned tree and cache directory, whose producer writes a real block
    ///     at the requested path and attaches the next queued catch-up loss, if any.
    /// </summary>
    sealed class LossScriptedCache : IDisposable
    {
        const uint VolumeSerial = 0x5EED;

        readonly string _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");

        public LossScriptedCache()
        {
            Directory.CreateDirectory(_treeRoot);
        }

        public Queue<JournalCheckpointLoss> Losses { get; } = new();

        public int Productions { get; private set; }

        /// <summary>The production number (1-based) whose producer call throws instead of writing a block.</summary>
        public int? FailProductionNumber { get; set; }

        public FakeIndexWatchSource Source { get; } = new();

        public static string CanonicalBlockName => CacheDirectory.BlockFileName('T', VolumeSerial);

        public FileIndexOptions Options() => new()
        {
            Drives = [new IndexedDrive('T', _treeRoot, VolumeSerial)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = Produce,
            WatchSource = Source
        };

        /// <summary>The cache directory's block files, without their owner-lock siblings.</summary>
        public string?[] BlockFileNames() => Directory.GetFiles(_cacheDirectory)
            .Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToArray();

        public void Dispose()
        {
            foreach (var directory in new[] { _treeRoot, _cacheDirectory })
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                    // A just-unmapped block file can stay locked briefly on Windows.
                }
            }
        }

        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken cancellationToken)
        {
            Productions++;
            if (Productions == FailProductionNumber)
            {
                throw new IOException("the retry scan failed");
            }

            MftBlockFixture.Write(request.BlockPath, request.VolumeSerial,
                journalId: 7, nextUsn: 4096, moment: MftBlockFixture.SeededMoment);
            var loss = Losses.Count > 0 ? Losses.Dequeue() : null;
            return Task.FromResult(new MftBlockProduceResult(
                BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false)
            {
                CatchUpLoss = loss
            });
        }
    }
}
