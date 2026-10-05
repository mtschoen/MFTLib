using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// Exceeds ~500 lines to keep cohesive MFT producer selection and rescan recovery tests together.
namespace MFTLib.Tests.Index;

/// <summary>
///     How <see cref="FileIndex.OpenAsync" /> selects between the MFT producer and the
///     enumeration producer. MFT failures mark only that drive failed, while explicit
///     enumeration ignores the MFT producer entirely. Every fake producer writes a small
///     MFT-shaped block directly through
///     <see cref="BlockFile" /> and <see cref="BlockWriter" />, so these tests run on Linux too.
/// </summary>
[TestClass]
public class FileIndexProducerSelectionTests
{
    static readonly DateTime FixedMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    OwnedIndexDirectories _directories = null!;
    string _treeRoot = null!;
    string _cacheDirectory = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directories = new OwnedIndexDirectories();
        _treeRoot = _directories.TreeRoot;
        _cacheDirectory = _directories.CacheDirectory;
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        File.WriteAllText(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello");
    }

    [TestCleanup]
    public void Cleanup()
    {
        _directories.Dispose();
    }

    FileIndexOptions Options(ProducerPolicy producerPolicy, MftBlockProducer? mftProducer)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = producerPolicy,
            MftSource = mftProducer is null ? null : new MftIndexSource(mftProducer)
        };
    }

    [TestMethod]
    public async Task Mft_SkippedRecordsAreSeparateFromAccessDeniedSubtreesAndResetOnRescan()
    {
        var skippedRecordCount = new[] { 3 };
        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken _) =>
            Task.FromResult(new MftBlockProduceResult(
                SeededBlocks.Build(request, 7, 4096, FixedMoment), 7, 4096, skippedRecordCount[0]));

        var options = Options(ProducerPolicy.Mft, Produce);
        await using (var index = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            Assert.AreEqual(3, index.Drives.Single().SkippedRecordCount);
            Assert.AreEqual(0, index.Drives.Single().AccessDeniedSubtreeCount);

            skippedRecordCount[0] = 0;
            await index.RescanAsync('T', CancellationToken.None);

            Assert.AreEqual(0, index.Drives.Single().SkippedRecordCount);
            Assert.AreEqual(0, index.Drives.Single().AccessDeniedSubtreeCount);
        }
    }

    [TestMethod]
    public async Task Mft_MarksTheFailedDriveAndKeepsTheOthers()
    {
        var firstRoot = Path.Combine(_treeRoot, "first");
        var secondRoot = Path.Combine(_treeRoot, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 1)), "invalid",
            TestContext.CancellationTokenSource.Token);
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', firstRoot, 1), new IndexedDrive('U', secondRoot, 2)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = new MftIndexSource((request, _) => request.DriveLetter == 'T'
                ? throw new UnauthorizedAccessException("elevation declined")
                : Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, 4096, FixedMoment),
                    7, 4096, 0)))
        }, TestContext.CancellationTokenSource.Token);

        var failed = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual("elevation declined", failed.MftProducerFailureMessage);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, failed.FailureKind);
        var ready = index.Drives.Single(drive => drive.DriveLetter == 'U');
        Assert.AreEqual(DriveState.Ready, ready.State);
        Assert.AreEqual(DriveFailureKind.None, ready.FailureKind);
    }

    [TestMethod]
    public async Task Mft_FailedDriveDoesNotLeakItsFailureMessageToAWarmStartedDrive()
    {
        var firstRoot = Path.Combine(_treeRoot, "first");
        var secondRoot = Path.Combine(_treeRoot, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        Directory.CreateDirectory(_cacheDirectory);
        using (SeededBlocks.Build(new MftBlockProduceRequest
        {
            DriveLetter = 'U',
            VolumeSerial = 2,
            BlockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('U', 2))
        }, 7, 4096, FixedMoment))
        {
        }

        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', firstRoot, 1), new IndexedDrive('U', secondRoot, 2)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = new MftIndexSource((_, _) => throw new UnauthorizedAccessException("elevation declined"))
        }, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual("elevation declined",
            index.Drives.Single(drive => drive.DriveLetter == 'T').MftProducerFailureMessage);
        var warmStarted = index.Drives.Single(drive => drive.DriveLetter == 'U');
        Assert.AreEqual(DriveState.Ready, warmStarted.State);
        Assert.IsNull(warmStarted.MftProducerFailureMessage);
    }

    [TestMethod]
    public async Task Mft_NeverWalksTheDirectoryTreeAfterAProducerFailure()
    {
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "should-not-be-indexed.txt"), "x",
            TestContext.CancellationTokenSource.Token);
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = new MftIndexSource((_, _) => throw new UnauthorizedAccessException("elevation declined"))
        }, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(DriveState.Failed, index.Drives.Single().State);
        Assert.AreEqual(0, index.Search(new SearchQuery(null)).Count);
    }

    /// <summary>
    ///     <see cref="FileIndexOptions.InitialOpenCacheOnly" />'s refusal contract: with no
    ///     usable cache present, the drive is failed with the cache-only refusal message and the
    ///     producer is never invoked. Asserting the invocation count, not just the end state, is
    ///     what distinguishes "refused before scanning" from "scanned and happened to fail".
    /// </summary>
    [TestMethod]
    public async Task Mft_WithInitialOpenCacheOnlyAndNoUsableCache_FailsTheDriveWithoutInvokingTheProducer()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> CountingProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalId: 7, nextUsn: 4096, moment: FixedMoment),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0));
        }

        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = new MftIndexSource(CountingProducer),
            InitialOpenCacheOnly = true
        }, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(0, invocationCount, "a cache-only open must never attempt a scan");
        var failed = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(
            "Drive T: no usable cache (missing, corrupt, or incompatible) and --cache-only forbids a scan.",
            failed.MftProducerFailureMessage);
        Assert.AreEqual(DriveFailureKind.CacheDeclined, failed.FailureKind);
    }


    [TestMethod]
    public async Task DefaultPolicy_WithNoProducerIsAConfigurationError()
    {
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory
        }, TestContext.CancellationTokenSource.Token));
    }

    [TestMethod]
    public async Task Enumeration_IgnoresAConfiguredMftProducerEntirely()
    {
        var invocationCount = 0;
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "indexed.txt"), "x",
            TestContext.CancellationTokenSource.Token);
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration,
            MftSource = new MftIndexSource((_, _) => { invocationCount++; throw new InvalidOperationException("the MFT producer must not be called"); })
        }, TestContext.CancellationTokenSource.Token);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(ProducerKind.Enumeration, index.HeaderOf().ProducerKind);
        Assert.IsNull(status.MftProducerFailureMessage);
        Assert.IsNotNull(index.Find(Path.Combine(_treeRoot, "indexed.txt")));
        Assert.AreEqual(0, invocationCount);
    }

    [TestMethod]
    public async Task Mft_StillPropagatesCancellation()
    {
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = new MftIndexSource(static (_, token) => throw new OperationCanceledException(token))
        }, TestContext.CancellationTokenSource.Token));
    }

    [TestMethod]
    public async Task RescanAsync_MftProducerFailurePreservesThePreviousBlockAndWarmCache()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken _)
        {
            if (++invocationCount > 1)
            {
                throw new UnauthorizedAccessException("elevation declined during rescan");
            }

            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, 4096, FixedMoment),
                7, 4096, 2));
        }

        var options = Options(ProducerPolicy.Mft, Produce);
        await using (var index = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(
                () => index.RescanAsync('T', CancellationToken.None));

            Assert.AreEqual(4096L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
            Assert.AreEqual("elevation declined during rescan", index.Drives.Single().MftProducerFailureMessage);
            Assert.AreEqual(BlockSource.ProducedByScan, index.Drives.Single().BlockSource);
            Assert.AreEqual(2, index.Drives.Single().SkippedRecordCount);
            Assert.AreEqual(0, index.Drives.Single().AccessDeniedSubtreeCount);
        }

        await using var reopened = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(4096L, reopened.Root('T').DriveBlock.Block.Header.UsnNextUsn);
        Assert.AreEqual(DriveState.Ready, reopened.Drives.Single().State);
        Assert.AreEqual(0, reopened.Drives.Single().SkippedRecordCount);
        Assert.AreEqual(0, reopened.Drives.Single().AccessDeniedSubtreeCount);
    }

    [TestMethod]
    public async Task RescanAsync_MftProducerRecoveryClearsTheFailureMessage()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            if (invocationCount == 2)
            {
                throw new UnauthorizedAccessException("elevation declined during rescan");
            }

            var nextUsn = 4096L * invocationCount;
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, nextUsn, FixedMoment),
                7, nextUsn, 0));
        }

        await using var index = await FileIndex.OpenAsync(Options(ProducerPolicy.Mft, Produce),
            CancellationToken.None);
        await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(
            () => index.RescanAsync('T', CancellationToken.None));
        Assert.AreEqual("elevation declined during rescan", index.Drives.Single().MftProducerFailureMessage);

        await index.RescanAsync('T', CancellationToken.None);

        Assert.IsNull(index.Drives.Single().MftProducerFailureMessage);
        Assert.AreEqual(12288L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
    }

    [TestMethod]
    public async Task RescanAsync_Mft_InvokesProducerAgainAndAdoptsItsCursor()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken cancellationToken)
        {
            var nextUsn = 4096L * ++invocationCount;
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, nextUsn, FixedMoment),
                7, nextUsn, 0));
        }

        await using var index = await FileIndex.OpenAsync(Options(ProducerPolicy.Mft, Produce),
            CancellationToken.None);
        var previousRoot = index.Root('T');

        await index.RescanAsync('T', CancellationToken.None);

        Assert.AreEqual(2, invocationCount);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
        Assert.IsTrue(index.Drives[0].WatchSupported);
        Assert.AreEqual(8192L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
        Assert.AreEqual(4096L, previousRoot.DriveBlock.Block.Header.UsnNextUsn);
        Assert.AreEqual(_treeRoot, previousRoot.Path);
    }

    [TestMethod]
    public async Task OpenAsync_MftWithAFakeProducer_OpensAnMftProducedDriveWithWatchSupport()
    {
        Task<MftBlockProduceResult> FakeProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalId: 7, nextUsn: 4096, moment: FixedMoment),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0));
        }

        var options = Options(ProducerPolicy.Mft, FakeProducer);
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
        Assert.IsTrue(index.Drives[0].WatchSupported);
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
    }

    [TestMethod]
    public async Task OpenAsync_MftWithACursorMismatchedProducer_DeletesTheRejectedBlockSoALaterOpenDoesNotWarmStartFromIt()
    {
        Task<MftBlockProduceResult> MismatchedProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalId: 7, nextUsn: 4096, moment: FixedMoment),
                JournalId: 99, NextUsn: 12345, SkippedRecordCount: 0));
        }

        var options = Options(ProducerPolicy.Mft, MismatchedProducer);

        await using (var failedIndex = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            Assert.AreEqual(DriveState.Failed, failedIndex.Drives.Single().State);
            StringAssert.Contains(failedIndex.Drives.Single().MftProducerFailureMessage, "journal cursor");
        }

        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
        Assert.IsFalse(File.Exists(blockPath),
            "a block rejected for a journal cursor mismatch must not survive to be warm-started by a later open");

        var invocationCount = 0;
        Task<MftBlockProduceResult> CountingProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalId: 7, nextUsn: 4096, moment: FixedMoment),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0));
        }

        var recoveryOptions = Options(ProducerPolicy.Mft, CountingProducer);
        await using var index = await FileIndex.OpenAsync(recoveryOptions, CancellationToken.None);

        Assert.AreEqual(1, invocationCount,
            "the producer must run again rather than the index warm-starting from the rejected block");
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
    }


    /// <summary>
    ///     Close-record coalescing state is owned by the drive block, so a rescan that
    ///     replaces the block resets it: the same close record the old block would have
    ///     suppressed is a first-sighted create on the replacement.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_ResetsReportedReasonCyclesWithTheReplacedBlock()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken cancellationToken)
        {
            var nextUsn = 4096L * ++invocationCount;
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, nextUsn, FixedMoment),
                7, nextUsn, 0));
        }

        await using var index = await FileIndex.OpenAsync(Options(ProducerPolicy.Mft, Produce),
            TestContext.CancellationTokenSource.Token);
        var created = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 20,
            ParentRecordNumber = 5,
            FileName = "tracked.txt",
            Reason = UsnReason.FileCreate,
            Usn = 5000,
            TimestampUtc = FixedMoment,
            FileAttributes = FileAttributes.Archive
        });
        var closed = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 20,
            ParentRecordNumber = 5,
            FileName = "tracked.txt",
            Reason = UsnReason.FileCreate | UsnReason.Close,
            Usn = 5001,
            TimestampUtc = FixedMoment,
            FileAttributes = FileAttributes.Archive
        });

        var controlCreated = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 21,
            ParentRecordNumber = 5,
            FileName = "control.txt",
            Reason = UsnReason.FileCreate,
            Usn = 5000,
            TimestampUtc = FixedMoment,
            FileAttributes = FileAttributes.Archive
        });
        var controlClosed = UsnJournalEntry.Create(new UsnJournalEntryOptions
        {
            RecordNumber = 21,
            ParentRecordNumber = 5,
            FileName = "control.txt",
            Reason = UsnReason.FileCreate | UsnReason.Close,
            Usn = 5001,
            TimestampUtc = FixedMoment,
            FileAttributes = FileAttributes.Archive
        });

        // Prove coalescing works within an unreplaced block's open cycle.
        Assert.AreEqual(1, index.ApplyJournalEntries('T', [controlCreated], 7, 5002).Count);
        Assert.AreEqual(0, index.ApplyJournalEntries('T', [controlClosed], 7, 5003).Count);

        // Record 20's create leaves its cycle open in ReportedReasonCycles leading up to rescan.
        Assert.AreEqual(1, index.ApplyJournalEntries('T', [created], 7, 5004).Count);

        await index.RescanAsync('T', TestContext.CancellationTokenSource.Token);

        // On the replaced block, the open cycle was discarded with the old DriveBlock;
        // the close record is treated as a first-sighted create rather than coalesced.
        var afterRescan = index.ApplyJournalEntries('T', [closed], 7, 8193);
        Assert.AreEqual(1, afterRescan.Count);
        Assert.AreEqual(FileChangeKind.Created, afterRescan[0].Kind);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDrive_ScansItAndClearsTheFailureKind()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> Producer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalId: 7, nextUsn: 4096, moment: FixedMoment),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0));
        }

        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = new MftIndexSource(Producer),
            InitialOpenCacheOnly = true
        }, TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(0, invocationCount, "a cache-only open must never attempt a scan");
        Assert.AreEqual(DriveState.Failed, index.Drives.Single().State);
        Assert.AreEqual(DriveFailureKind.CacheDeclined, index.Drives.Single().FailureKind);

        await index.RescanAsync('T', TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(1, invocationCount);
        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(DriveFailureKind.None, status.FailureKind);
        Assert.IsNull(status.MftProducerFailureMessage);
        Assert.AreEqual(BlockSource.ProducedByScan, status.BlockSource);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
        Assert.IsTrue(status.WatchSupported);
        Assert.AreEqual(4096L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedDriveWhoseScanFails_StaysFailedAsProducerFailed()
    {
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = new MftIndexSource((_, _) => throw new UnauthorizedAccessException("elevation declined")),
            InitialOpenCacheOnly = true
        }, TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(DriveFailureKind.CacheDeclined, index.Drives.Single().FailureKind);

        await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(
            () => index.RescanAsync('T', TestContext.CancellationTokenSource.Token));

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, status.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.FailureKind);
        Assert.AreEqual("elevation declined", status.MftProducerFailureMessage);
    }

    [TestMethod]
    public async Task RescanAsync_ProducerFailedDrive_ScansItAndClearsTheFailureKind()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> Producer(MftBlockProduceRequest request, CancellationToken _)
        {
            if (++invocationCount == 1)
            {
                throw new UnauthorizedAccessException("elevation declined");
            }

            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalId: 7, nextUsn: 4096, moment: FixedMoment),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0));
        }

        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftSource = new MftIndexSource(Producer)
        }, TestContext.CancellationTokenSource.Token);
        Assert.AreEqual(DriveState.Failed, index.Drives.Single().State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, index.Drives.Single().FailureKind);

        await index.RescanAsync('T', TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(2, invocationCount);
        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(DriveFailureKind.None, status.FailureKind);
        Assert.IsNull(status.MftProducerFailureMessage);
        Assert.AreEqual(BlockSource.ProducedByScan, status.BlockSource);
    }

    [TestMethod]
    public async Task OpenAsync_MftWithACursorMismatchedProducer_LogsTheRejectedBlocksDelete()
    {
        Task<MftBlockProduceResult> MismatchedProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalId: 7, nextUsn: 4096, moment: FixedMoment),
                JournalId: 99, NextUsn: 12345, SkippedRecordCount: 0));
        }

        var deletions = new List<string>();
        var options = Options(ProducerPolicy.Mft, MismatchedProducer) with { Diagnostics = deletions.Add };
        await using var failedIndex = await FileIndex.OpenAsync(options,
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(DriveState.Failed, failedIndex.Drives.Single().State);
        var blockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', 0x0BADF00D));
        var line = deletions.SingleOrDefault(entry => entry.Contains(blockPath));
        Assert.IsNotNull(line, "the rejected producer block's delete must be logged with its path");
        StringAssert.Contains(line, "journal cursor");
    }
}
