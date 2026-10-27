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

    string CreateDriveRoot(string name)
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

    static void AssertSettled(FileIndex index, char driveLetter, BlockOrigin? blockOrigin, DriveState state)
    {
        var status = index.Status;
        Assert.AreEqual(new VolumeKey(driveLetter), status.Drive);
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
            AssertSettled(indexU, 'U', BlockOrigin.ProducedByScan, DriveState.Ready);
            Assert.IsFalse(openingT.IsCompleted, "U completing its open does not depend on T");
        }
        finally
        {
            heldT.Release();
        }

        await using var indexT = await openingT.WaitAsync(HangGuard, TestContext.CancellationToken);
        AssertSettled(indexT, 'T', BlockOrigin.ProducedByScan, DriveState.Ready);
    }

    [TestMethod]
    public async Task OpenAsync_CacheOnlyDeclinedDrive_ReportsItsBlocklessState()
    {
        var warmDrive = MFTLibTestExtensions.SyntheticIndexedDrive.Create(new VolumeKey('T'), CreateDriveRoot("first"), 1);
        var coldDrive = MFTLibTestExtensions.SyntheticIndexedDrive.Create(new VolumeKey('U'), CreateDriveRoot("second"), 2);

        await using (await FileIndex.OpenAsync(Options(warmDrive), TestContext.CancellationToken))
        {
        }

        await using var warmIndex = await FileIndex.OpenAsync(Options(warmDrive, cacheOnly: true), TestContext.CancellationToken);
        await using var coldIndex = await FileIndex.OpenAsync(Options(coldDrive, cacheOnly: true), TestContext.CancellationToken);

        AssertSettled(warmIndex, 'T', BlockOrigin.WarmStartedFromCache, DriveState.Ready);
        AssertSettled(coldIndex, 'U', null, DriveState.Failed);
    }

    [TestMethod]
    public async Task OpenAsync_OfflineDrive_ReportsOfflineWithNoBlock()
    {
        var drive = MFTLibTestExtensions.SyntheticIndexedDrive.Create(new VolumeKey('Z'), Path.Combine(TreeRoot, "absent"), 1);

        await using var index = await FileIndex.OpenAsync(Options(drive), TestContext.CancellationToken);

        AssertSettled(index, 'Z', null, DriveState.Offline);
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

        var drive = MFTLibTestExtensions.SyntheticIndexedDrive.Create(new VolumeKey('T'), CreateDriveRoot("failing"), 1);
        var options = Options(drive, producer: ProducerKind.Mft, mftProducer: FailingProducer);
        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        AssertSettled(index, 'T', null, DriveState.Failed);
        Assert.Contains("simulated MFT producer failure", index.Status.Failure?.Message!);
    }
}
