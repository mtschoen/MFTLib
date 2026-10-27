using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static MFTLib.Tests.TestSupport.CheckpointLossFixture;

namespace MFTLib.Tests.Index;

/// <summary>
///     Telling apart the two moments a journal position can be found missing, which is what a
///     <see cref="FileIndex.WatchFaulted" /> handler needs to know before it blames the journal.
///     A report from the open survives an unrelated watch fault, because the fault did not make
///     it untrue, and stays labelled as the open's; a report from the watch replaces it, because
///     it is a newer fact about the same root.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FileIndexCheckpointLossDetectionTests : IDisposable
{
    public TestContext TestContext { get; set; } = null!;

    CheckpointLossFixture _fixture = null!;

    CancellationToken Token => TestContext.CancellationToken;

    [TestInitialize]
    public void Initialize() => _fixture = new CheckpointLossFixture();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    ///     Opens over a cache whose checkpoint the journal has trimmed away, so the volume
    ///     cold-scans and carries a report from the open before any watch has run.
    /// </summary>
    async Task<FileIndex> OpenWithAnOpenTimeLossAsync(IIndexWatchSource watchSource)
    {
        await _fixture.Seed(_fixture.Options(watchSource: watchSource), TestContext.CancellationToken);
        using var journal = JournalWindows.Journal(firstUsn: CachedNextUsn + 500, nextUsn: CachedNextUsn + 4_000);
        var index = await FileIndex.OpenAsync(_fixture.Options(watchSource: watchSource), CancellationToken.None);
        try
        {
            var loss = index.Status.Watch.CheckpointLoss;
            Assert.IsNotNull(loss, "this fixture needs a report from the open to start from");
            Assert.AreEqual(500L, loss.BytesBehind);
            return index;
        }
        catch
        {
            await index.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    ///     The regression. An unrelated watch fault must not turn the open's report into a
    ///     claim about that fault, and must not delete it either: the root really was
    ///     cold-scanned because the journal had moved on, and a revoked handle later does not
    ///     make that false.
    /// </summary>
    [TestMethod]
    public async Task AnUnrelatedWatchFault_LeavesTheOpensReportIntactAndStillLabelledAsTheOpens()
    {
        var source = new ScriptedWatchSource();
        await using var index = await OpenWithAnOpenTimeLossAsync(source.IndexSource);
        index.HoldEveryRecovery();
        var openReport = index.Status.Watch.CheckpointLoss;
        Assert.IsNotNull(openReport);

        // The cold scan wrote a fresh cursor and the journal holds it, so the volume is healthy
        // now. Only the report from the open remains.
        using var journal = JournalWindows.Journal(firstUsn: 0, nextUsn: CachedNextUsn + 4_000);
        await index.StartWatchingAsync(Token);

        await FailVolumeAndWaitForFaultAsync(index, source,
            new UnauthorizedAccessException("the volume handle was revoked"));

        var root = index.Status;
        Assert.IsNotNull(root.Watch.FailureMessage, "the watch still reports that it died");
        Assert.AreEqual(WatchState.Recovering, root.Watch.State);

        var loss = root.Watch.CheckpointLoss;
        Assert.IsNotNull(loss, "an unrelated fault must not delete a true report from the open");
        Assert.AreEqual(JournalCheckpointLossDetection.RootOpening, loss.DetectedDuring,
            "the report still describes the open, so a fault handler must not read it as its own");
        Assert.AreEqual(openReport, loss, "nothing about the open's report changed");

        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(
            () => index.StopWatchingAsync(Token));
    }

    /// <summary>
    ///     The other half: a watch fault where the position really is gone is a newer fact
    ///     about the same root, so it replaces the open's report rather than sitting behind it.
    /// </summary>
    [TestMethod]
    public async Task AWatchTimeLoss_ReplacesTheOpensReportAndIsLabelledAsTheWatchs()
    {
        var source = new ScriptedWatchSource();
        await using var index = await OpenWithAnOpenTimeLossAsync(source.IndexSource);
        index.HoldEveryRecovery();

        await index.StartWatchingAsync(Token);

        // A window distinct from the open's, so the numbers say which detection produced them.
        using var journal = JournalWindows.Journal(firstUsn: CachedNextUsn + 900, nextUsn: CachedNextUsn + 8_000);
        await FailVolumeAndWaitForFaultAsync(index, source,
            new IOException("USN journal entries have been deleted; full rescan needed"));

        var loss = index.Status.Watch.CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, loss.DetectedDuring,
            "the newer fact about this root is the one a fault handler acts on");
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual(900L, loss.BytesBehind, "the watch's window, not the open's");
        Assert.AreEqual(CachedNextUsn + 8_000, loss.NextUsn);

        await RootResultAssertions.FailureAsync<VolumeWatchFaultException>(() => index.StopWatchingAsync(Token));
    }

    /// <summary>
    ///     Fails root <c>T</c>'s watch through its handle and returns once the index has
    ///     recorded the fault and announced it, which is after the checkpoint-loss check ran.
    /// </summary>
    static async Task FailVolumeAndWaitForFaultAsync(FileIndex index, ScriptedWatchSource source,
        Exception failure)
    {
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(WatchFault fault) => announced.TrySetResult();
        index.WatchFaulted += Observe;
        try
        {
            source.WatchFor('T').FailVolume(failure);
            await announced.Task.WaitAsync(HangGuard);
        }
        finally
        {
            index.WatchFaulted -= Observe;
        }
    }

    /// <summary>A report from the open says so even when no watch has ever run.</summary>
    [TestMethod]
    public async Task AReportFromTheOpen_IsLabelledAsTheOpens()
    {
        await using var index = await OpenWithAnOpenTimeLossAsync(new ScriptedWatchSource().IndexSource);

        Assert.AreEqual(JournalCheckpointLossDetection.RootOpening,
            index.Status.Watch.CheckpointLoss!.DetectedDuring);
    }
}
