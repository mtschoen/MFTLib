using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

// Which producer an open selects from the volume facts and ProducerSelection: Automatic scans the MFT when the volume is
// eligible and otherwise walks with the reason on Status, MftOnly fails before any cache work, WalkOnly never touches the source.
public sealed partial class FileIndexProducerSelectionTests
{
    static readonly VolumeKey Volume = new('T');

    sealed class CountingSource
    {
        int _producerCalls;

        public int ProducerCalls => Volatile.Read(ref _producerCalls);

        public MftIndexSource Source => new((request, _) =>
        {
            Interlocked.Increment(ref _producerCalls);
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, 4096, FixedMoment), 0));
        });
    }

    VolumeFacts EligibleFacts(DriveType driveType = DriveType.Fixed) =>
        new(true, Volume, TreeRoot, UniqueVolumeSerial, true, driveType, "NTFS");

    FileIndexOptions OpenWith(ProducerSelection selection, VolumeFacts facts, MftIndexSource? source,
        CacheMode? cacheMode = null) => new()
        {
            Root = Volume,
            Producer = selection,
            CacheDirectory = CacheDirectoryPath,
            CacheMode = cacheMode,
            MftSource = source,
            VolumeFacts = (_, _) => facts
        };

    // The four reasons that are facts about the volume; NoMftSource is judged separately because it is not one.
    VolumeFacts Ineligible(MftUnavailableReason reason) => reason switch
    {
        MftUnavailableReason.PlatformWithoutMft => EligibleFacts() with { PlatformHasMft = false },
        MftUnavailableReason.VolumeNotReady => EligibleFacts() with { IsReady = false },
        MftUnavailableReason.NotLocalVolume => EligibleFacts(DriveType.Network),
        MftUnavailableReason.NotNtfs => EligibleFacts() with { Format = "exFAT" },
        _ => EligibleFacts()
    };

    string[] MftBlockFiles() =>
        Directory.Exists(CacheDirectoryPath) ? [.. Directory.GetFiles(CacheDirectoryPath, "*.mlix")] : [];

    [TestMethod]
    [DataRow(MftUnavailableReason.PlatformWithoutMft)]
    [DataRow(MftUnavailableReason.VolumeNotReady)]
    [DataRow(MftUnavailableReason.NotLocalVolume)]
    [DataRow(MftUnavailableReason.NotNtfs)]
    [DataRow(MftUnavailableReason.NoMftSource)]
    public async Task Automatic_AnIneligibleVolumeWalksAndReportsTheReason(MftUnavailableReason reason)
    {
        var counting = new CountingSource();
        var source = reason == MftUnavailableReason.NoMftSource ? null : counting.Source;

        await using var index = await FileIndex.OpenAsync(
            OpenWith(ProducerSelection.Automatic, Ineligible(reason), source), TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(RootState.Ready, status.State);
        Assert.AreEqual(ProducerKind.Enumeration, status.Producer);
        Assert.AreEqual(reason, status.MftUnavailable);
        Assert.AreEqual(ProducerKind.Enumeration, index.HeaderOf().ProducerKind);
        Assert.AreEqual(0, counting.ProducerCalls);
    }

    [TestMethod]
    [DataRow(MftUnavailableReason.PlatformWithoutMft)]
    [DataRow(MftUnavailableReason.VolumeNotReady)]
    [DataRow(MftUnavailableReason.NotLocalVolume)]
    [DataRow(MftUnavailableReason.NotNtfs)]
    [DataRow(MftUnavailableReason.NoMftSource)]
    public async Task MftOnly_AnIneligibleVolumeFailsBeforeCacheWork(MftUnavailableReason reason)
    {
        var counting = new CountingSource();
        var source = reason == MftUnavailableReason.NoMftSource ? null : counting.Source;

        await using var index = await FileIndex.OpenAsync(
            OpenWith(ProducerSelection.MftOnly, Ineligible(reason), source), TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(RootState.Failed, status.State);
        Assert.AreEqual(RootFailureKind.MftUnavailable, status.Failure?.Kind);
        Assert.AreEqual(reason, status.MftUnavailable);
        Assert.AreEqual(ProducerKind.Mft, status.Producer);
        Assert.IsEmpty(MftBlockFiles());
        Assert.AreEqual(0, counting.ProducerCalls);
    }

    [TestMethod]
    public async Task MftOnly_OnAPlatformWithoutAnMft_ReportsThePlatformEvenWhenTheRootsHostPathDoesNotExist()
    {
        var noPlatformMft = new VolumeFacts(false, null, null, 0, false, DriveType.Unknown, null);
        var options = OpenWith(ProducerSelection.MftOnly, noPlatformMft, null) with { Root = new VolumeKey('Q') };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(RootState.Failed, status.State);
        Assert.AreEqual(RootFailureKind.MftUnavailable, status.Failure?.Kind);
        Assert.AreEqual(MftUnavailableReason.PlatformWithoutMft, status.MftUnavailable);
        Assert.Contains("Windows only", status.Failure!.Message);
    }

    [TestMethod]
    public async Task WalkOnly_TouchesNoMftSource()
    {
        var counting = new CountingSource();

        await using var index = await FileIndex.OpenAsync(
            OpenWith(ProducerSelection.WalkOnly, EligibleFacts(), counting.Source), TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(RootState.Ready, status.State);
        Assert.AreEqual(ProducerKind.Enumeration, status.Producer);
        Assert.IsNull(status.MftUnavailable);
        Assert.AreEqual(0, counting.ProducerCalls);
    }

    [TestMethod]
    public async Task Automatic_EligibleVolumeScansTheMft()
    {
        var counting = new CountingSource();

        await using var index = await FileIndex.OpenAsync(
            OpenWith(ProducerSelection.Automatic, EligibleFacts(), counting.Source), TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(RootState.Ready, status.State);
        Assert.AreEqual(ProducerKind.Mft, status.Producer);
        Assert.IsNull(status.MftUnavailable);
        Assert.AreEqual(1, counting.ProducerCalls);
    }

    [TestMethod]
    public async Task Automatic_EligibleRemovableVolumeScansTheMft()
    {
        var counting = new CountingSource();

        await using var index = await FileIndex.OpenAsync(
            OpenWith(ProducerSelection.Automatic, EligibleFacts(DriveType.Removable), counting.Source),
            TestContext.CancellationToken);

        Assert.AreEqual(ProducerKind.Mft, index.Status.Producer);
        Assert.AreEqual(1, counting.ProducerCalls);
    }

    [TestMethod]
    public async Task Automatic_MidScanFailureFailsAndNeverWalks()
    {
        var source = new MftIndexSource((_, _) => throw new IOException("the volume went away"));

        await using var index = await FileIndex.OpenAsync(
            OpenWith(ProducerSelection.Automatic, EligibleFacts(), source), TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(RootState.Failed, status.State);
        Assert.AreEqual(RootFailureKind.ProducerFailed, status.Failure?.Kind);
        Assert.AreEqual(ProducerKind.Mft, status.Producer);
        Assert.IsNull(status.MftUnavailable);
        Assert.IsNull(status.Block);
        Assert.IsEmpty(MftBlockFiles(), "a failed MFT scan must not be replaced by a walked block");
    }

    [TestMethod]
    public async Task Rescan_KeepsTheWalkOfAnAutomaticIndex()
    {
        var counting = new CountingSource();
        await using var index = await FileIndex.OpenAsync(
            OpenWith(ProducerSelection.Automatic, Ineligible(MftUnavailableReason.NotNtfs), counting.Source),
            TestContext.CancellationToken);

        var result = await index.RescanAsync(TestContext.CancellationToken);

        Assert.AreEqual(RootOperationOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(ProducerKind.Enumeration, index.Status.Producer);
        Assert.AreEqual(ProducerKind.Enumeration, index.HeaderOf().ProducerKind);
        Assert.AreEqual(MftUnavailableReason.NotNtfs, index.Status.MftUnavailable);
        Assert.AreEqual(0, counting.ProducerCalls);
    }

    [TestMethod]
    public async Task Rescan_OfAnIneligibleMftOnlyIndex_ReturnsTheSameFailureWithoutTheSource()
    {
        var counting = new CountingSource();
        await using var index = await FileIndex.OpenAsync(
            OpenWith(ProducerSelection.MftOnly, Ineligible(MftUnavailableReason.NotNtfs), counting.Source),
            TestContext.CancellationToken);
        var openFailure = index.Status.Failure;

        await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.RescanAsync(TestContext.CancellationToken));

        var status = index.Status;
        Assert.AreEqual(RootState.Failed, status.State);
        Assert.AreEqual(openFailure, status.Failure);
        Assert.AreEqual(MftUnavailableReason.NotNtfs, status.MftUnavailable);
        Assert.AreEqual(0, counting.ProducerCalls);
    }

    [TestMethod]
    public async Task Offline_AtOpen_HasNoProducer()
    {
        var facts = EligibleFacts() with { VolumeRootDirectory = Path.Combine(TreeRoot, "absent") };

        await using var index = await FileIndex.OpenAsync(
            OpenWith(ProducerSelection.Automatic, facts, new CountingSource().Source), TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(RootState.Offline, status.State);
        Assert.IsNull(status.Producer);
        Assert.IsNull(status.MftUnavailable);
    }

    [TestMethod]
    public async Task Mft_CacheFileIsNamedForTheVolume()
    {
        await using var index = await FileIndex.OpenAsync(
            OpenWith(ProducerSelection.Automatic, EligibleFacts(), new CountingSource().Source),
            TestContext.CancellationToken);

        var expected = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(Volume, UniqueVolumeSerial));
        Assert.AreSequenceEqual([expected], MftBlockFiles());
    }

    [TestMethod]
    public async Task NullSource_CachedMftBlockAdoptsAsMftAndItsRescanFailsWithoutWalking()
    {
        await using (await FileIndex.OpenAsync(
                         OpenWith(ProducerSelection.Automatic, EligibleFacts(), new CountingSource().Source),
                         TestContext.CancellationToken))
        {
        }

        await using var index = await FileIndex.OpenAsync(
            OpenWith(ProducerSelection.Automatic, EligibleFacts(), null), TestContext.CancellationToken);

        var opened = index.Status;
        Assert.AreEqual(RootState.Ready, opened.State);
        Assert.AreEqual(ProducerKind.Mft, opened.Producer);
        Assert.IsNull(opened.MftUnavailable);
        Assert.AreEqual(BlockOrigin.WarmStartedFromCache, opened.Block!.Origin);

        await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.RescanAsync(TestContext.CancellationToken));

        var rescanned = index.Status;
        Assert.AreEqual(RootState.Ready, rescanned.State);
        Assert.AreEqual(RootFailureKind.MftUnavailable, rescanned.Failure?.Kind);
        Assert.AreEqual(MftUnavailableReason.NoMftSource, rescanned.MftUnavailable);
        Assert.AreEqual(ProducerKind.Mft, rescanned.Producer);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind, "the producer is fixed, so the retained block stays an MFT block");
    }

    [TestMethod]
    public async Task NullSource_ExistingOnlyWithNoCachedBlock_DeclinesWithoutWalking()
    {
        await using var index = await FileIndex.OpenAsync(
            OpenWith(ProducerSelection.Automatic, EligibleFacts(), null, CacheMode.ExistingOnly),
            TestContext.CancellationToken);

        var status = index.Status;
        Assert.AreEqual(RootState.Failed, status.State);
        Assert.AreEqual(RootFailureKind.CacheDeclined, status.Failure?.Kind);
        Assert.AreEqual(ProducerKind.Mft, status.Producer);
        Assert.IsEmpty(MftBlockFiles());
    }
}
