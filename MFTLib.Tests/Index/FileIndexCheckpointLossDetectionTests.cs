using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Telling apart the two moments a journal position can be found missing, which is what a
///     <see cref="FileIndex.WatchFaulted" /> handler needs to know before it blames the journal.
///     A report from the open survives an unrelated watch fault, because the fault did not make
///     it untrue, and stays labelled as the open's; a report from the watch replaces it, because
///     it is a newer fact about the same drive.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexCheckpointLossDetectionTests
{
    const ulong CachedJournalId = 0xABCD;
    const long CachedNextUsn = 1_000_000;
    const long AllocationDelta = 64;
    const long MaximumSize = 128L * 1024 * 1024;
    static readonly DateTime FixedMoment = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

    public TestContext TestContext { get; set; } = null!;

    string _treeRoot = null!;
    string _cacheDirectory = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    [TestInitialize]
    public void Initialize()
    {
        _treeRoot = Path.Combine(Path.GetTempPath(), $"mftlib-tree-{Guid.NewGuid():N}");
        _cacheDirectory = Path.Combine(Path.GetTempPath(), $"mftlib-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
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

    FileIndexOptions Options(IIndexWatchSource watchSource)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = ProduceMftShapedBlock,
            WatchSource = watchSource
        };
    }

    /// <summary>An MFT-kind block carrying the checkpoint a warm start would resume from.</summary>
    static Task<MftBlockProduceResult> ProduceMftShapedBlock(
        MftBlockProduceRequest request, CancellationToken cancellationToken)
    {
        var createOptions = new BlockFileCreateOptions
        {
            Path = request.BlockPath,
            VolumeSerial = request.VolumeSerial,
            ProducerKind = ProducerKind.Mft,
            RootRow = 5,
            SlotCapacity = BlockLayout.ComputeSlotCapacity(8),
            NamePoolCapacity = BlockLayout.ComputeNamePoolCapacity(256),
            DeleteOnClose = request.DeleteOnClose
        };

        using (var block = BlockFile.Create(createOptions))
        {
            var writer = new BlockWriter(block);
            writer.TryWriteRow(0, "$MFT",
                new RowColumns(ParentRow: 0, Flags: RowFlags.InUse, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            writer.TryWriteRow(5, ".",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks, SequenceNumber: 0));
            writer.SetJournalCursor(CachedJournalId, CachedNextUsn);
            writer.Complete(FixedMoment);
        }

        return Task.FromResult(new MftBlockProduceResult(
            BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
            CachedJournalId, CachedNextUsn, SkippedRecordCount: 0, CompactionNeeded: false));
    }

    static IDisposable Journal(long firstUsn, long nextUsn)
    {
        return JournalCheckpointCheck.OverrideJournalForTest(
            _ => new JournalWindow(CachedJournalId, firstUsn, nextUsn, AllocationDelta, MaximumSize));
    }

    /// <summary>Writes the cache block a later open will try to warm-start from.</summary>
    async Task SeedCacheAsync(IIndexWatchSource watchSource)
    {
        using var journal = Journal(firstUsn: 0, nextUsn: CachedNextUsn);
        await using var index = await FileIndex.OpenAsync(Options(watchSource), CancellationToken.None);
        Assert.IsNull(index.Drives.Single().CheckpointLoss);
    }

    /// <summary>
    ///     Opens over a cache whose checkpoint the journal has trimmed away, so the drive
    ///     cold-scans and carries a report from the open before any watch has run.
    /// </summary>
    async Task<FileIndex> OpenWithAnOpenTimeLossAsync(IIndexWatchSource watchSource)
    {
        await SeedCacheAsync(watchSource);
        using var journal = Journal(firstUsn: CachedNextUsn + 500, nextUsn: CachedNextUsn + 4_000);
        var index = await FileIndex.OpenAsync(Options(watchSource), CancellationToken.None);
        try
        {
            var loss = index.Drives.Single().CheckpointLoss;
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
    ///     claim about that fault, and must not delete it either: the drive really was
    ///     cold-scanned because the journal had moved on, and a revoked handle later does not
    ///     make that false.
    /// </summary>
    [TestMethod]
    public async Task AnUnrelatedWatchFault_LeavesTheOpensReportIntactAndStillLabelledAsTheOpens()
    {
        using var source = new FakeIndexWatchSource();
        await using var index = await OpenWithAnOpenTimeLossAsync(source);
        var openReport = index.Drives.Single().CheckpointLoss;
        Assert.IsNotNull(openReport);

        // The cold scan wrote a fresh cursor and the journal holds it, so the drive is healthy
        // now. Only the report from the open remains.
        using var journal = Journal(firstUsn: 0, nextUsn: CachedNextUsn + 4_000);
        await index.StartWatchingAsync(Token);
        await source.SourceStartedAsync();

        await source.PublishAsync(new DriveWatchFailure('T',
            new UnauthorizedAccessException("the volume handle was revoked")));

        var drive = index.Drives.Single();
        Assert.IsNotNull(drive.WatchFailureMessage, "the watch still reports that it died");
        Assert.AreEqual(WatchCatchUpState.Faulted, drive.WatchCatchUp);

        var loss = drive.CheckpointLoss;
        Assert.IsNotNull(loss, "an unrelated fault must not delete a true report from the open");
        Assert.AreEqual(JournalCheckpointLossDetection.DriveOpening, loss.DetectedDuring,
            "the report still describes the open, so a fault handler must not read it as its own");
        Assert.AreEqual(openReport, loss, "nothing about the open's report changed");

        await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(
            () => index.StopWatchingAsync(Token));
    }

    /// <summary>
    ///     The other half: a watch fault where the position really is gone is a newer fact
    ///     about the same drive, so it replaces the open's report rather than sitting behind it.
    /// </summary>
    [TestMethod]
    public async Task AWatchTimeLoss_ReplacesTheOpensReportAndIsLabelledAsTheWatchs()
    {
        using var source = new FakeIndexWatchSource();
        await using var index = await OpenWithAnOpenTimeLossAsync(source);

        await index.StartWatchingAsync(Token);
        await source.SourceStartedAsync();

        // A window distinct from the open's, so the numbers say which detection produced them.
        using var journal = Journal(firstUsn: CachedNextUsn + 900, nextUsn: CachedNextUsn + 8_000);
        await source.PublishAsync(new DriveWatchFailure('T',
            new IOException("USN journal entries have been deleted; full rescan needed")));

        var loss = index.Drives.Single().CheckpointLoss;
        Assert.IsNotNull(loss);
        Assert.AreEqual(JournalCheckpointLossDetection.LiveWatch, loss.DetectedDuring,
            "the newer fact about this drive is the one a fault handler acts on");
        Assert.AreEqual(JournalCheckpointLossCause.CheckpointTrimmed, loss.Cause);
        Assert.AreEqual(900L, loss.BytesBehind, "the watch's window, not the open's");
        Assert.AreEqual(CachedNextUsn + 8_000, loss.NextUsn);

        await Assert.ThrowsExceptionAsync<IOException>(() => index.StopWatchingAsync(Token));
    }

    /// <summary>A report from the open says so even when no watch has ever run.</summary>
    [TestMethod]
    public async Task AReportFromTheOpen_IsLabelledAsTheOpens()
    {
        using var source = new FakeIndexWatchSource();
        await using var index = await OpenWithAnOpenTimeLossAsync(source);

        Assert.AreEqual(JournalCheckpointLossDetection.DriveOpening,
            index.Drives.Single().CheckpointLoss!.DetectedDuring);
    }
}
