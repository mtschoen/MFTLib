using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Automatic falls back to a walk when the MFT cannot be reached at an open's cold scan (elevation declined, broker
///     unavailable), MftOnly fails instead, and neither ever walks after the MFT producer started and failed. The broker
///     session here is the production one with a scripted launch.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexMftFallbackTests : IndexTreeTestBase
{
    static readonly DateTime FixedMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    static readonly VolumeKey VolumeT = new('T');

    public TestContext TestContext { get; set; } = null!;

    FileIndexOptions Options(ProducerSelection selection, MftIndexSource source, RootKey? root = null,
        string? volumeRoot = null, VolumeKey? volume = null, uint? serial = null) =>
        SyntheticVolumeFacts.Apply(new FileIndexOptions
        {
            Root = root ?? VolumeT,
            Producer = selection,
            CacheDirectory = CacheDirectoryPath,
            MftSource = source
        }, serial ?? UniqueVolumeSerial, volume: volume ?? VolumeT, volumeRootDirectory: volumeRoot ?? TreeRoot);

    static BrokerSession FailingSession(Exception failure, Action? onLaunch = null) =>
        new(_ =>
        {
            onLaunch?.Invoke();
            return Task.FromException<BrokerProcess>(failure);
        });

    // A source over the session that signals once its connect call has joined the session's launch.
    static MftIndexSource JoiningSource(BrokerSession session, TaskCompletionSource joined) =>
        new BrokerMftBlockProducer(cancellationToken =>
        {
            var connecting = session.ConnectAsync(cancellationToken);
            joined.TrySetResult();
            return connecting;
        }).CreateIndexSource();

    static MftIndexSource SeededSource() => new((request, _) =>
        Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, 4096, FixedMoment), 0)));

    [TestMethod]
    public async Task Automatic_ElevationDeclined_WalksAndReportsIt()
    {
        await using var session = FailingSession(new BrokerLaunchDeclinedException("declined"));

        await using var index = await FileIndex.OpenAsync(
            Options(ProducerSelection.Automatic, session.CreateIndexSource()), TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(RootState.Ready, status.State);
        Assert.AreEqual(ProducerKind.Enumeration, status.Producer);
        Assert.AreEqual(MftUnavailableReason.ElevationDeclined, status.MftUnavailable);
        Assert.IsNull(status.Failure);
        Assert.AreEqual(ProducerKind.Enumeration, index.HeaderOf().ProducerKind);
    }

    [TestMethod]
    public async Task Automatic_ConnectTimeout_WalksAsBrokerUnavailable()
    {
        await using var session = FailingSession(new TimeoutException("never connected"));

        await using var index = await FileIndex.OpenAsync(
            Options(ProducerSelection.Automatic, session.CreateIndexSource()), TestContext.CancellationToken);

        Assert.AreEqual(RootState.Ready, index.Status.State);
        Assert.AreEqual(ProducerKind.Enumeration, index.Status.Producer);
        Assert.AreEqual(MftUnavailableReason.BrokerUnavailable, index.Status.MftUnavailable);
    }

    [TestMethod]
    public async Task MftOnly_ElevationDeclined_Fails()
    {
        await using var session = FailingSession(new BrokerLaunchDeclinedException("declined"));

        await using var index = await FileIndex.OpenAsync(
            Options(ProducerSelection.MftOnly, session.CreateIndexSource()), TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(RootState.Failed, status.State);
        Assert.AreEqual(RootFailureKind.MftUnavailable, status.Failure?.Kind);
        Assert.AreEqual(MftUnavailableReason.ElevationDeclined, status.MftUnavailable);
        Assert.AreEqual(ProducerKind.Mft, status.Producer);
        Assert.IsNull(status.Block);
    }

    [TestMethod]
    public async Task Automatic_ConcurrentOpensThatFallBack_ShareOneLaunch()
    {
        var firstRoot = Path.Combine(TreeRoot, "first");
        var secondRoot = Path.Combine(TreeRoot, "second");
        foreach (var directory in new[] { firstRoot, secondRoot })
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "readme.md"), "hello", TestContext.CancellationToken);
        }

        var launches = 0;
        var launchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new BrokerSession(async _ =>
        {
            Interlocked.Increment(ref launches);
            launchEntered.TrySetResult();
            await release.Task;
            throw new BrokerLaunchDeclinedException("declined");
        });
        var source = session.CreateIndexSource();

        var openingT = FileIndex.OpenAsync(
            Options(ProducerSelection.Automatic, source, volumeRoot: firstRoot), TestContext.CancellationToken);
        await launchEntered.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        // The second open joins the launch in flight: the signal fires after ConnectAsync has taken its place on it.
        var secondJoined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSource = JoiningSource(session, secondJoined);
        var openingU = FileIndex.OpenAsync(Options(ProducerSelection.Automatic, secondSource, root: new VolumeKey('U'),
            volume: new VolumeKey('U'), volumeRoot: secondRoot, serial: UniqueVolumeSerial + 1), TestContext.CancellationToken);
        await secondJoined.Task.WaitAsync(HangGuard, TestContext.CancellationToken);
        release.SetResult();
        await using var indexT = await openingT.WaitAsync(HangGuard, TestContext.CancellationToken);
        await using var indexU = await openingU.WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.AreEqual(1, launches, "one elevation prompt serves both opens");
        foreach (var index in new[] { indexT, indexU })
        {
            Assert.AreEqual(RootState.Ready, index.Status.State);
            Assert.AreEqual(ProducerKind.Enumeration, index.Status.Producer);
            Assert.AreEqual(MftUnavailableReason.ElevationDeclined, index.Status.MftUnavailable);
        }
    }

    [TestMethod]
    public async Task Automatic_DirectoryRootFallback_TakesItsWalkSlotAndReleasesTheVolumeSlot()
    {
        var directory = Path.Combine(TreeRoot, "src");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "a.txt"), "a", TestContext.CancellationToken);
        var directoryKey = RootKey.FromDirectory(directory);
        await using var session = FailingSession(new BrokerLaunchDeclinedException("declined"));

        await using var walked = await FileIndex.OpenAsync(
            Options(ProducerSelection.Automatic, session.CreateIndexSource(), root: directoryKey),
            TestContext.CancellationToken);

        Assert.AreEqual(ProducerKind.Enumeration, walked.Status.Producer);
        var files = Directory.GetFiles(CacheDirectoryPath, "*.mlix").Select(Path.GetFileName).ToArray();
        Assert.AreSequenceEqual([CacheDirectory.BlockFileName(directoryKey, UniqueVolumeSerial)], files);

        await using var volumeIndex = await FileIndex.OpenAsync(
            Options(ProducerSelection.MftOnly, SeededSource()), TestContext.CancellationToken);

        Assert.AreEqual(CacheLocation.OwnedCanonical, volumeIndex.Status.Block!.CacheLocation,
            "the failed attempt released the volume slot instead of holding its owner lock");
    }

    [TestMethod]
    public async Task Automatic_WarmMftBlock_LaunchesNoBroker()
    {
        using var journals = JournalWindows.ForVolumes(
            new Dictionary<VolumeKey, SyntheticJournalWindow> { [VolumeT] = JournalWindows.Window(7, 0, 4096) });
        await using (await FileIndex.OpenAsync(Options(ProducerSelection.Automatic, SeededSource()),
                         TestContext.CancellationToken))
        {
        }

        var launches = 0;
        await using var session = FailingSession(new BrokerLaunchDeclinedException("declined"), () => launches++);

        await using var index = await FileIndex.OpenAsync(
            Options(ProducerSelection.Automatic, session.CreateIndexSource()), TestContext.CancellationToken);

        Assert.AreEqual(0, launches);
        Assert.AreEqual(ProducerKind.Mft, index.Status.Producer);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, index.Status.Block!.Origin);
        Assert.IsNull(index.Status.MftUnavailable);
    }

    [TestMethod]
    public async Task Rescan_LaunchFailureOnAnMftIndex_KeepsTheBlockAndNeverWalks()
    {
        var calls = 0;
        var source = new MftIndexSource((request, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                throw new MftUnavailableException(MftUnavailableReason.ElevationDeclined, "declined");
            }

            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, 4096, FixedMoment), 0));
        });
        await using var index = await FileIndex.OpenAsync(
            Options(ProducerSelection.Automatic, source), TestContext.CancellationToken);
        Assert.AreEqual(ProducerKind.Mft, index.Status.Producer);

        await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.RescanAsync(TestContext.CancellationToken));

        var failed = index.Status;
        Assert.AreEqual(RootState.Ready, failed.State);
        Assert.AreEqual(RootFailureKind.MftUnavailable, failed.Failure?.Kind);
        Assert.AreEqual(MftUnavailableReason.ElevationDeclined, failed.MftUnavailable);
        Assert.AreEqual(ProducerKind.Mft, failed.Producer);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind, "the producer is fixed, so the retained block stays");

        await index.RescanAsync(TestContext.CancellationToken);

        var recovered = index.Status;
        Assert.IsNull(recovered.Failure);
        Assert.IsNull(recovered.MftUnavailable);
        Assert.AreEqual(3, calls);
    }
}
