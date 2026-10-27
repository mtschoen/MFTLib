using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CheckpointLossFixture;
using static MFTLib.Tests.TestSupport.JournalWindows;

namespace MFTLib.Tests.Index;

/// <summary>
///     A cache-only open adopts a block despite a lost journal checkpoint rather than failing
///     the root (see <see cref="FileIndexCheckpointLossLifetimeTests" />), because a cache-only
///     open never watches and the block is still a correct snapshot as of its age. But starting a
///     live watch from that block's cursor would resume from a position the journal no longer
///     holds, so <see cref="FileIndex.StartWatchingAsync" /> refuses such
///     a root and says why, recording the watch as requested the way a start whose source threw
///     does. A successful <see cref="FileIndex.RescanAsync" /> writes a
///     fresh cursor, clears the refusal, and starts the requested watch. The journal
///     read is swapped out through <c>JournalIsolation.OverrideJournalWindow</c>, so these
///     run on every platform and never touch a real volume.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexCacheOnlyUnresumableWatchTests : IDisposable
{
    public TestContext TestContext { get; set; } = null!;

    CheckpointLossFixture _fixture = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestInitialize]
    public void Initialize() => _fixture = new CheckpointLossFixture('T', 'U');

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    ///     Starts a root whose block is unresumable and returns the refusal the consumer sees.
    ///     The refusal leaves the watch requested, so a stop or a rescan applies afterwards.
    /// </summary>
    static async Task<InvalidOperationException> RefuseStartAsync(FileIndex index, CancellationToken token)
    {
        var refusal = await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.StartWatchingAsync(token));
        Assert.Contains("RescanAsync", refusal.Message);
        return refusal;
    }

    [TestMethod]
    public async Task StartWatchingAsync_RefusesTheUnresumableRootAndWatchesTheHealthyOne()
    {
        var rootT = _fixture.Root();
        var rootU = _fixture.Root('U');
        await _fixture.Seed(_fixture.Options(root: rootT), TestContext.CancellationToken);
        await _fixture.Seed(_fixture.Options(root: rootU), TestContext.CancellationToken);

        var source = new ScriptedWatchSource();
        using var journals = ForVolumes(new Dictionary<VolumeKey, SyntheticJournalWindow> { [new VolumeKey('T')] = TrimmedWindow, [new VolumeKey('U')] = HealthyWindow });
        await using var indexT = await FileIndex.OpenAsync(
            _fixture.Options(watchSource: source.IndexSource, cacheMode: CacheMode.ExistingOnly, root: rootT), Token);
        await using var indexU = await FileIndex.OpenAsync(
            _fixture.Options(watchSource: source.IndexSource, cacheMode: CacheMode.ExistingOnly, root: rootU), Token);

        // Both roots are adopted (cache-only never fails a root for a lost checkpoint alone).
        Assert.AreEqual(RootState.Ready, indexT.Status.State);
        Assert.AreEqual(RootState.Ready, indexU.Status.State);
        Assert.IsNotNull(indexT.Status.Watch.CheckpointLoss);
        Assert.IsNull(indexU.Status.Watch.CheckpointLoss);

        await RefuseStartAsync(indexT, Token);
        await indexU.StartWatchingAsync(Token);

        Assert.AreEqual('U', source.Starts.Single().Volume,
            "the root whose checkpoint could not be resumed must never reach the watch source");

        var unresumable = indexT.Status;
        Assert.IsNotNull(unresumable.Watch.FailureMessage, "the refusal must be reported, not silent");
        Assert.Contains("RescanAsync", unresumable.Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, unresumable.Watch.State);

        var healthy = indexU.Status;
        Assert.IsNull(healthy.Watch.FailureMessage);
        Assert.AreEqual(WatchState.CatchingUp, healthy.Watch.State);

        await source.WatchFor('U').Publish(new JournalBatch(
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalIdentifier: CachedJournalIdentifier, NextUsn: 9_500));
        Assert.AreEqual(9_500L, indexU.Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);

        await indexU.StopWatchingAsync(Token);
    }

    /// <summary>
    ///     A refusal leaves the watch requested, so the rescan that replaces the block clears the
    ///     refusal and starts the watch from the fresh cursor, exactly as it would after a start
    ///     whose source threw.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_OnTheUnresumableRoot_ClearsTheRefusalAndStartsTheRequestedWatch()
    {
        var rootT = _fixture.Root();
        var rootU = _fixture.Root('U');
        await _fixture.Seed(_fixture.Options(root: rootT), TestContext.CancellationToken);
        await _fixture.Seed(_fixture.Options(root: rootU), TestContext.CancellationToken);

        var source = new ScriptedWatchSource();
        using var journals = ForVolumes(new Dictionary<VolumeKey, SyntheticJournalWindow> { [new VolumeKey('T')] = TrimmedWindow, [new VolumeKey('U')] = HealthyWindow });
        await using var indexT = await FileIndex.OpenAsync(
            _fixture.Options(watchSource: source.IndexSource, cacheMode: CacheMode.ExistingOnly, root: rootT), Token);
        await using var indexU = await FileIndex.OpenAsync(
            _fixture.Options(watchSource: source.IndexSource, cacheMode: CacheMode.ExistingOnly, root: rootU), Token);
        await indexU.StartWatchingAsync(Token);
        await RefuseStartAsync(indexT, Token);
        Assert.IsTrue(indexT.Status.Watch.IsRequested,
            "a refused start records the request, as a start whose source threw does");

        await indexT.RescanAsync(Token);

        Assert.AreEqual(new IndexWatchTarget(new VolumeKey('T'), CachedJournalIdentifier, CachedNextUsn), source.TargetsFor('T').Single(),
            "the rescan starts the requested watch from the fresh cursor");
        var recovered = indexT.Status;
        Assert.AreEqual(BlockOrigin.ProducedByScan, recovered.Block!.Origin);
        Assert.IsNull(recovered.Watch.CheckpointLoss, "the rescan replaced the block the report described");
        Assert.IsNull(recovered.Watch.FailureMessage);
        Assert.IsTrue(recovered.Watch.IsRequested);
        Assert.AreEqual(WatchState.CatchingUp, indexT.Status.Watch.State);
        await source.WatchFor('T').Publish(new JournalBatch(
            [WatchHarness.Create(recordNumber: 9, "after.txt")], JournalIdentifier: CachedJournalIdentifier, NextUsn: 5_000));
        Assert.AreEqual(5_000L, indexT.Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);

        await indexT.StopWatchingAsync(Token);
        await indexU.StopWatchingAsync(Token);
    }

    /// <summary>
    ///     A rescan whose producer returns no block replaces nothing and reports a failed result
    ///     carrying <see cref="InvalidOperationException" /> with the producer's failure, and the root's
    ///     refusal survives exactly as it was: the old, still-unresumable block is not started, and
    ///     the message explaining why is not cleared.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_OnTheUnresumableRoot_WhenTheProducerReturnsNoBlock_ReportsFailureAndLeavesTheRefusalIntact()
    {
        var rootT = _fixture.Root();
        var rootU = _fixture.Root('U');
        await _fixture.Seed(_fixture.Options(root: rootT), TestContext.CancellationToken);
        await _fixture.Seed(_fixture.Options(root: rootU), TestContext.CancellationToken);

        var scanFailure = new IOException("synthetic scan failure for T");
        Task<MftBlockProduceResult> Producer(MftBlockProduceRequest request, CancellationToken token)
        {
            // The producer throws, and the scanning code turns that into a scan with no block.
            return char.ToUpperInvariant((request.Volume).Letter) == 'T'
                ? throw scanFailure
                : ProduceMftShapedBlock(request, token);
        }

        var source = new ScriptedWatchSource();
        using var journals = ForVolumes(new Dictionary<VolumeKey, SyntheticJournalWindow> { [new VolumeKey('T')] = TrimmedWindow, [new VolumeKey('U')] = HealthyWindow });
        await using var indexT = await FileIndex.OpenAsync(
            _fixture.Options(Producer, source.IndexSource, CacheMode.ExistingOnly, rootT), Token);
        await using var indexU = await FileIndex.OpenAsync(
            _fixture.Options(Producer, source.IndexSource, CacheMode.ExistingOnly, rootU), Token);
        await indexU.StartWatchingAsync(Token);
        await RefuseStartAsync(indexT, Token);

        var before = indexT.Status;
        Assert.IsNotNull(before.Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, before.Watch.State);
        Assert.IsNotNull(before.Watch.CheckpointLoss);

        var failure = await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => indexT.RescanAsync(Token));

        Assert.AreSame(scanFailure, failure.InnerException);
        Assert.IsEmpty(source.TargetsFor('T'),
            "a failed rescan must not start the cursor the journal still cannot resume");

        var after = indexT.Status;
        Assert.AreEqual("synthetic scan failure for T", after.Failure?.Message);
        Assert.IsNotNull(after.Watch.FailureMessage, "the refusal must stay reported after a failed scan");
        Assert.Contains("RescanAsync", after.Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, after.Watch.State);
        Assert.IsNotNull(after.Watch.CheckpointLoss, "the block did not change, so the original loss still explains it");
        Assert.AreEqual(before.Watch.CheckpointLoss, after.Watch.CheckpointLoss);

        // U is unaffected by T's failed rescan.
        await source.WatchFor('U').Publish(new JournalBatch(
            [WatchHarness.Create(recordNumber: 9, "u.txt")], JournalIdentifier: CachedJournalIdentifier, NextUsn: 9_000));
        Assert.AreEqual(9_000L, indexU.Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);

        await indexU.StopWatchingAsync(Token);
    }

    /// <summary>
    ///     A scripted producer replaces a cached MFT block with an unwatchable block on rescan.
    ///     The restart defensively withdraws the request
    ///     and faults a wait issued between the retired watch and that restart.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_ThatReplacesAWatchedBlockWithAnUnwatchableOne_WithdrawsTheRequestAndFaultsTheWait()
    {
        var rootT = _fixture.Root();
        await _fixture.Seed(_fixture.Options(root: rootT), TestContext.CancellationToken);
        var source = new ScriptedWatchSource();
        using var journals = ForVolumes(new Dictionary<VolumeKey, SyntheticJournalWindow> { [new VolumeKey('T')] = HealthyWindow });
        BlockFile? producedBlock = null;
        var index = await FileIndex.OpenAsync(_fixture.Options(async (request, cancellationToken) =>
        {
            var result = await ProduceMftShapedBlock(request, cancellationToken);
            producedBlock = result.Block;
            return result;
        }, source.IndexSource, root: rootT), Token);
        index.Hooks.PublishInsideWriteGate = _ => producedBlock?.Header.ProducerKind = ProducerKind.Enumeration;
        try
        {
            Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind, "the cached MFT block warm-started");
            await index.StartWatchingAsync(Token);
            Task<RootOperationResult>? wait = null;
            index.Hooks.BeforeRestartDecision = _ => wait ??= index.WaitForCatchUpAsync(CancellationToken.None);

            await RootResultAssertions.FailureAsync<InvalidOperationException>(() => index.RescanAsync(Token));

            Assert.IsNotNull(wait);
            var failure = await RootResultAssertions.FailureAsync<InvalidOperationException>(
                () => wait.WaitAsync(HangGuard, TestContext.CancellationToken));
            Assert.Contains("no MFT-backed block", failure.Message);
            var status = index.Status;
            Assert.AreEqual(ProducerKind.Enumeration, index.HeaderOf().ProducerKind);
            Assert.IsFalse(status.Watch.IsRequested);
            Assert.AreEqual(WatchState.NotStarted, status.Watch.State);
            Assert.HasCount(1, source.TargetsFor('T'));
        }
        finally
        {
            // Disposed explicitly, after the restart seam that captures the index can no longer run.
            await index.DisposeAsync();
        }
    }

    /// <summary>
    ///     The same rescan over a watch that had already faulted on its channel, which the rescan
    ///     retains until the restart: the restart supersedes that faulted watch with its failure,
    ///     so the root reads exactly as it does after a healthy watch.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_ThatReplacesAChannelFaultedWatchsBlockWithAnUnwatchableOne_ReadsNotStarted()
    {
        var rootT = _fixture.Root();
        await _fixture.Seed(_fixture.Options(root: rootT), TestContext.CancellationToken);
        var source = new ScriptedWatchSource();
        using var journals = ForVolumes(new Dictionary<VolumeKey, SyntheticJournalWindow> { [new VolumeKey('T')] = HealthyWindow });
        BlockFile? producedBlock = null;
        await using var index = await FileIndex.OpenAsync(_fixture.Options(async (request, cancellationToken) =>
        {
            var result = await ProduceMftShapedBlock(request, cancellationToken);
            producedBlock = result.Block;
            return result;
        }, source.IndexSource, root: rootT), Token);
        index.Hooks.PublishInsideWriteGate = _ => producedBlock?.Header.ProducerKind = ProducerKind.Enumeration;
        var channelFaulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        index.WatchFaulted += fault =>
        {
            if (fault.Kind == WatchFaultKind.Channel)
            {
                channelFaulted.TrySetResult();
            }
        };
        await index.StartWatchingAsync(Token);
        source.WatchFor('T').LoseChannel(new IOException("the channel went away"));
        await channelFaulted.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        Assert.AreEqual(WatchState.Faulted, index.Status.Watch.State);

        await RootResultAssertions.FailureAsync<InvalidOperationException>(() => index.RescanAsync(Token));

        var status = index.Status;
        Assert.AreEqual(ProducerKind.Enumeration, index.HeaderOf().ProducerKind);
        Assert.IsFalse(status.Watch.IsRequested);
        Assert.AreEqual(WatchState.NotStarted, status.Watch.State);
        Assert.IsNull(status.Watch.FailureMessage, "the superseded watch's channel failure no longer describes the volume");
        await RootResultAssertions.NotApplicableAsync(() => index.WaitForCatchUpAsync(Token));
        await RootResultAssertions.NotApplicableAsync(() => index.StopWatchingAsync(Token));
        Assert.HasCount(1, source.TargetsFor('T'));
    }

    [TestMethod]
    public async Task StartWatchingAsync_WhenTheOnlyMftRootIsUnresumable_RefusesItAndReportsIt()
    {
        var rootT = _fixture.Root();
        await _fixture.Seed(_fixture.Options(root: rootT), TestContext.CancellationToken);

        var source = new ScriptedWatchSource();
        using var journals = ForVolumes(new Dictionary<VolumeKey, SyntheticJournalWindow> { [new VolumeKey('T')] = TrimmedWindow });
        await using var index = await FileIndex.OpenAsync(
            _fixture.Options(watchSource: source.IndexSource, cacheMode: CacheMode.ExistingOnly, root: rootT), Token);

        var refusal = await RefuseStartAsync(index, Token);

        Assert.IsEmpty(source.Starts, "no root survived to reach the watch source");
        var status = index.Status;
        Assert.AreEqual(refusal.Message, status.Watch.FailureMessage);
        Assert.AreEqual(WatchState.Faulted, status.Watch.State);

        // The refusal is the root's last start failure, so a catch-up wait faults with it.
        var waitFailure = await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.WaitForCatchUpAsync(Token));
        Assert.Contains("RescanAsync", waitFailure.Message);

        // The refusal records the request, so a stop withdraws it without reporting the refusal again,
        // which the start already reported; after that there is nothing left to stop.
        Assert.IsTrue(status.Watch.IsRequested);
        await index.StopWatchingAsync(Token);
        var stopped = index.Status;
        Assert.IsFalse(stopped.Watch.IsRequested);
        Assert.AreEqual(WatchState.NotStarted, stopped.Watch.State);
        await RootResultAssertions.NotApplicableAsync(() => index.StopWatchingAsync(Token));
    }
}
