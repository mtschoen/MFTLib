using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CatchUpLossTestSupport;

namespace MFTLib.Tests.Index;

/// <summary>
///     The open's settle runs the same scan operation: a root whose catch-up is lost at open is
///     scanned again at once, and after the limit it settles <see cref="RootState.Ready" /> with its
///     last block, unresumable, while <see cref="FileIndex.OpenAsync" /> does not throw. Each lost block
///     a retry supersedes leaves no file behind.
/// </summary>
public partial class FileIndexCatchUpLossTests
{
    [TestMethod]
    public async Task Open_CatchUpLostOnce_RetriesAndSettlesReady()
    {
        using var cache = new LossScriptedCache();
        cache.Losses.Enqueue(StandardCatchUpLoss('T'));

        await using var index = await FileIndex.OpenAsync(cache.Options(), Token);

        Assert.AreEqual(2, cache.Productions);
        var root = index.Status;
        Assert.AreEqual(RootState.Ready, root.State);
        Assert.AreEqual(BlockOrigin.ProducedByScan, root.Block!.Origin);
        Assert.AreEqual(0, root.Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(root.Watch.IsRecoveryStopped);
        Assert.AreEqual(StandardCatchUpLoss('T'), root.Watch.CheckpointLoss, "the retry keeps the report the loss produced");
        Assert.AreEqual(WatchState.NotStarted, root.Watch.State);
        Assert.AreSequenceEqual([LossScriptedCache.CanonicalBlockName], cache.BlockFileNames());
        await index.StartWatchingAsync(Token);
        Assert.HasCount(1, cache.Source.TargetsFor('T'), "the retried block is resumable");
    }

    [TestMethod]
    public async Task OpenAsync_ThreeLostCatchUps_SettlesReadyAndRefusesTheWatch()
    {
        using var cache = new LossScriptedCache();
        cache.Losses.Enqueue(StandardCatchUpLoss('T'));
        cache.Losses.Enqueue(StandardCatchUpLoss('T'));
        cache.Losses.Enqueue(StandardCatchUpLoss('T'));

        await using var index = await FileIndex.OpenAsync(cache.Options(), Token);

        Assert.AreEqual(3, cache.Productions);
        var root = index.Status;
        Assert.AreEqual(RootState.Ready, root.State);
        Assert.AreEqual(3, root.Watch.ConsecutiveLostCatchUps);
        Assert.IsTrue(root.Watch.IsRecoveryStopped, "exhaustion is visible before open completes");
        Assert.AreEqual(StandardCatchUpLoss('T'), root.Watch.CheckpointLoss);
        Assert.AreEqual(WatchState.Faulted, root.Watch.State);
        Assert.Contains("12288", root.Watch.FailureMessage!);
        Assert.AreSequenceEqual([LossScriptedCache.CanonicalBlockName], cache.BlockFileNames());
        var refusal = await RootResultAssertions.FailureAsync<InvalidOperationException>(() => index.StartWatchingAsync(Token));
        Assert.Contains("RescanAsync", refusal.Message);
    }

    [TestMethod]
    public async Task Open_CatchUpLostThenTheRetryScanFails_SettlesReadyWithTheLostBlockAndTheFailure()
    {
        using var cache = new LossScriptedCache();
        cache.Losses.Enqueue(StandardCatchUpLoss('T'));
        cache.FailProductionNumber = 2;

        await using var index = await FileIndex.OpenAsync(cache.Options(), Token);

        Assert.AreEqual(2, cache.Productions);
        var root = index.Status;
        Assert.AreEqual(RootState.Ready, root.State, "the lost block stays in place");
        Assert.AreEqual(1, root.Watch.ConsecutiveLostCatchUps);
        Assert.IsFalse(root.Watch.IsRecoveryStopped);
        Assert.AreEqual(StandardCatchUpLoss('T'), root.Watch.CheckpointLoss);
        Assert.AreEqual("the retry scan failed", root.Failure?.Message);
        Assert.AreSequenceEqual([LossScriptedCache.CanonicalBlockName], cache.BlockFileNames());
        var refusal = await RootResultAssertions.FailureAsync<InvalidOperationException>(() => index.StartWatchingAsync(Token));
        Assert.Contains("RescanAsync", refusal.Message);
    }

    /// <summary>
    ///     One MFT volume over an owned tree and cache directory, whose producer writes a real block
    ///     at the requested path and attaches the next queued catch-up loss, if any.
    /// </summary>
    sealed class LossScriptedCache : IDisposable
    {
        const uint VolumeSerial = 0x5EED;

        readonly IndexTreeFixture _fixture = new(seedDocuments: false);
        string CacheDirectoryPath => _fixture.CacheDirectory;

        public Queue<JournalCheckpointLoss> Losses { get; } = new();

        public int Productions { get; private set; }

        /// <summary>The production number (1-based) whose producer call throws instead of writing a block.</summary>
        public int? FailProductionNumber { get; set; }

        public ScriptedWatchSource Source { get; } = new();

        public static string CanonicalBlockName => CacheDirectory.BlockFileName(new VolumeKey('T'), VolumeSerial);

        public FileIndexOptions Options() =>
            _fixture.Options(volumeSerial: VolumeSerial, producer: ProducerSelection.MftOnly) with
            {
                MftSource = new MftIndexSource(Produce, Source.IndexSource)
            };

        /// <summary>The cache directory's block files, without their owner-lock siblings.</summary>
        public string?[] BlockFileNames() => [.. Directory.GetFiles(CacheDirectoryPath)
            .Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .Select(Path.GetFileName)];

        public void Dispose() => _fixture.Dispose();

        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken cancellationToken)
        {
            Productions++;
            if (Productions == FailProductionNumber)
            {
                throw new IOException("the retry scan failed");
            }

            SeededBlocks.Write(request.BlockPath, request.VolumeSerial,
                journalIdentifier: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment);
            var loss = Losses.Count > 0 ? Losses.Dequeue() : null;
            return Task.FromResult(new MftBlockProduceResult(
                BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
                SkippedRecordCount: 0)
            {
                CatchUpLoss = loss
            });
        }
    }
}
