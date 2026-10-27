using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Each <see cref="FileIndex.OpenAsync" /> call owns and settles one root independently:
///     a root parked in its producer does not delay another root sharing the same
///     <see cref="MftIndexSource" /> and cache directory, and offline or failed roots report their
///     settled <see cref="RootStatus" /> directly from the returned <see cref="FileIndex" />.
/// </summary>
[TestClass]
public sealed class FileIndexOpenProgressTests : IndexTreeTestBase
{
    public TestContext TestContext { get; set; } = null!;

    protected override bool SeedDocuments => false;

    string CreateRoot(string name)
    {
        var root = Path.Combine(TreeRoot, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "readme.md"), "hello");
        return root;
    }

    FileIndexOptions Options(IndexedRoot root, bool cacheOnly = false,
        ProducerKind producer = ProducerKind.Enumeration, MftBlockProducer? mftProducer = null) =>
        Fixture.Options(producer: producer,
            cacheMode: cacheOnly ? CacheMode.ExistingOnly : CacheMode.Persistent) with
        {
            Root = root with { Producer = producer },
            MftSource = mftProducer is null ? null : new MftIndexSource(mftProducer)
        };

    static void AssertSettled(FileIndex index, char driveLetter, BlockOrigin? blockOrigin, RootState state)
    {
        var status = index.Status;
        Assert.AreEqual(new VolumeKey(driveLetter), status.Root);
        Assert.AreEqual(blockOrigin, status.Block?.Origin);
        Assert.AreEqual(state, status.State);
    }

    [TestMethod]
    public async Task OpenAsync_HeldRootDoesNotBlockAnotherRootSharingSourceAndCacheDirectory()
    {
        using var scenario = new OpenScenario();
        var heldT = scenario.Hold('T');

        var openingT = FileIndex.OpenAsync(scenario.Options('T'), TestContext.CancellationToken);
        try
        {
            await heldT.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.IsFalse(openingT.IsCompleted, "T is still held in its producer");

            await using var indexU = await FileIndex.OpenAsync(scenario.Options('U'), TestContext.CancellationToken)
                .WaitAsync(HangGuard, TestContext.CancellationToken);
            AssertSettled(indexU, 'U', BlockOrigin.ProducedByScan, RootState.Ready);
            Assert.IsFalse(openingT.IsCompleted, "U completing its open does not depend on T");
        }
        finally
        {
            heldT.Release();
        }

        await using var indexT = await openingT.WaitAsync(HangGuard, TestContext.CancellationToken);
        AssertSettled(indexT, 'T', BlockOrigin.ProducedByScan, RootState.Ready);
    }

    [TestMethod]
    public async Task OpenAsync_CacheOnlyDeclinedRoot_ReportsItsBlocklessState()
    {
        var warmRoot = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), CreateRoot("first"), 1);
        var coldRoot = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('U'), CreateRoot("second"), 2);

        await using (await FileIndex.OpenAsync(Options(warmRoot), TestContext.CancellationToken))
        {
        }

        await using var warmIndex = await FileIndex.OpenAsync(Options(warmRoot, cacheOnly: true), TestContext.CancellationToken);
        await using var coldIndex = await FileIndex.OpenAsync(Options(coldRoot, cacheOnly: true), TestContext.CancellationToken);

        AssertSettled(warmIndex, 'T', BlockOrigin.WarmStartedFromCache, RootState.Ready);
        AssertSettled(coldIndex, 'U', null, RootState.Failed);
    }

    [TestMethod]
    public async Task OpenAsync_OfflineRoot_ReportsOfflineWithNoBlock()
    {
        var root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('Z'), Path.Combine(TreeRoot, "absent"), 1);

        await using var index = await FileIndex.OpenAsync(Options(root), TestContext.CancellationToken);

        AssertSettled(index, 'Z', null, RootState.Offline);
    }

    [TestMethod]
    public async Task OpenAsync_AsynchronousHandledProducerFailure_ReportsFailedState()
    {
        async Task<MftBlockProduceResult> FailingProducer(MftBlockProduceRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("simulated MFT producer failure");
        }

        var root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), CreateRoot("failing"), 1);
        var options = Options(root, producer: ProducerKind.Mft, mftProducer: FailingProducer);
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        AssertSettled(index, 'T', null, RootState.Failed);
        Assert.Contains("simulated MFT producer failure", index.Status.Failure?.Message!);
    }
}
