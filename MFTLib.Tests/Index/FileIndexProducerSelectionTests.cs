using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// Exceeds ~500 lines to keep cohesive MFT producer selection and rescan recovery tests together.
namespace MFTLib.Tests.Index;

/// <summary>
///     How <see cref="FileIndex.OpenAsync" /> selects between the MFT producer and the
///     enumeration producer. MFT failures mark only that volume failed, while explicit
///     enumeration ignores the MFT producer entirely. Every fake producer writes a small
///     MFT-shaped block directly through
///     <see cref="BlockFile" /> and <see cref="BlockWriter" />, so these tests run on Linux too.
/// </summary>
[TestClass]
public sealed class FileIndexProducerSelectionTests : IndexTreeTestBase
{
    static readonly DateTime FixedMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    public TestContext TestContext { get; set; } = null!;

    FileIndexOptions Options(ProducerKind producer, MftBlockProducer? mftProducer) =>
        Fixture.Options(producer: producer) with
        {
            MftSource = mftProducer is null ? null : new MftIndexSource(mftProducer)
        };

    [TestMethod]
    public async Task Mft_SkippedRecordsAreSeparateFromAccessDeniedSubtreesAndResetOnRescan()
    {
        var skippedRecordCount = new[] { 3 };
        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken _) =>
            Task.FromResult(new MftBlockProduceResult(
                SeededBlocks.Build(request, 7, 4096, FixedMoment), skippedRecordCount[0]));

        var options = Options(ProducerKind.Mft, Produce);
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(3, index.Status.Block!.SkippedRecordCount);
        Assert.AreEqual(0, index.Status.Block!.AccessDeniedSubtreeCount);

        skippedRecordCount[0] = 0;
        await index.RescanAsync(CancellationToken.None);

        Assert.AreEqual(0, index.Status.Block!.SkippedRecordCount);
        Assert.AreEqual(0, index.Status.Block!.AccessDeniedSubtreeCount);
    }

    [TestMethod]
    public async Task RetainedFileNames_OneSetReachesTheMftProducerAndFiltersTheEnumerationWalk()
    {
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Notes.TXT"), "keep", TestContext.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(Path.Combine(TreeRoot, "notes.txt"), "keep exact", TestContext.CancellationToken);
        }

        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "other.bin"), "drop", TestContext.CancellationToken);
        var seen = new List<IReadOnlyCollection<string>?>();
        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken _)
        {
            seen.Add(request.RetainedFileNames);
            return Task.FromResult(new MftBlockProduceResult(
                SeededBlocks.Build(request, 7, 4096, FixedMoment), 0));
        }

        string[] retained = ["notes.txt"];
        using var mftRoot = new TempDirectory("mftlib-mft-root");
        await using var mftIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('M'), mftRoot.Path, 2),
            CacheDirectory = CacheDirectoryPath,
            MftSource = new MftIndexSource(Produce),
            RetainedFileNames = retained
        }, TestContext.CancellationToken);
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), TreeRoot, 1, ProducerKind.Enumeration),
            CacheDirectory = CacheDirectoryPath,
            MftSource = new MftIndexSource(Produce),
            RetainedFileNames = retained
        }, TestContext.CancellationToken);

        Assert.AreSequenceEqual(retained, [.. seen.Single()!]);
        var walked = index.Status;
        Assert.AreEqual(RootState.Ready, walked.State);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsNotNull(index.Find(Path.Combine(TreeRoot, "Notes.TXT"), TestContext.CancellationToken));
        }
        else
        {
            Assert.IsNotNull(index.Find(Path.Combine(TreeRoot, "notes.txt"), TestContext.CancellationToken));
            Assert.IsNull(index.Find(Path.Combine(TreeRoot, "Notes.TXT"), TestContext.CancellationToken));
        }

        Assert.IsNull(index.Find(Path.Combine(TreeRoot, "other.bin"), TestContext.CancellationToken));
        Assert.IsNull(index.Find(Path.Combine(TreeRoot, "Documents", "readme.md"), TestContext.CancellationToken));
        Assert.IsTrue(index.Find(Path.Combine(TreeRoot, "Documents"), TestContext.CancellationToken)!.Value.IsDirectory);
    }

    [TestMethod]
    [DataRow("elevation declined")]
    [DataRow("Volume T: no MFT source is available to scan this volume.")]
    public async Task Mft_MarksTheFailedRootAndKeepsTheOthers(string failureMessage)
    {
        var firstRoot = Path.Combine(TreeRoot, "first");
        var secondRoot = Path.Combine(TreeRoot, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        Directory.CreateDirectory(CacheDirectoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('T'), 1)), "invalid",
            TestContext.CancellationToken);
        var source = new MftIndexSource((request, _) => request.Volume == new VolumeKey('T')
            ? throw new UnauthorizedAccessException(failureMessage)
            : Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, 4096, FixedMoment), 0)));
        await using var failedIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), firstRoot, 1),
            CacheDirectory = CacheDirectoryPath,
            MftSource = source
        }, TestContext.CancellationToken);
        await using var readyIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('U'), secondRoot, 2),
            CacheDirectory = CacheDirectoryPath,
            MftSource = source
        }, TestContext.CancellationToken);

        var failed = failedIndex.Status;
        Assert.AreEqual(RootState.Failed, failed.State);
        Assert.AreEqual(failureMessage, failed.Failure?.Message);
        Assert.AreEqual(RootFailureKind.ProducerFailed, failed.Failure?.Kind);
        Assert.IsNull(failed.Block);
        var ready = readyIndex.Status;
        Assert.AreEqual(RootState.Ready, ready.State);
        Assert.IsNull(ready.Failure);
        Assert.IsNotNull(ready.Block);
    }

    [TestMethod]
    public async Task Mft_FailedRootDoesNotLeakItsFailureMessageToAWarmStartedRoot()
    {
        var firstRoot = Path.Combine(TreeRoot, "first");
        var secondRoot = Path.Combine(TreeRoot, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        Directory.CreateDirectory(CacheDirectoryPath);
        using (SeededBlocks.Build(new MftBlockProduceRequest
        {
            Volume = 'U',
            VolumeSerial = 2,
            BlockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('U'), 2))
        }, 7, 4096, FixedMoment))
        {
        }

        var source = new MftIndexSource((_, _) => throw new UnauthorizedAccessException("elevation declined"));
        await using var failedIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), firstRoot, 1),
            CacheDirectory = CacheDirectoryPath,
            MftSource = source
        }, TestContext.CancellationToken);
        await using var warmIndex = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('U'), secondRoot, 2),
            CacheDirectory = CacheDirectoryPath,
            MftSource = source
        }, TestContext.CancellationToken);

        Assert.AreEqual("elevation declined", failedIndex.Status.Failure?.Message);
        var warmStarted = warmIndex.Status;
        Assert.AreEqual(RootState.Ready, warmStarted.State);
        Assert.IsNull(warmStarted.Failure);
    }

    /// <summary>
    ///     <see cref="CacheMode.ExistingOnly" />'s refusal contract: with no
    ///     usable cache present, the root is failed with the cache-only refusal message and the
    ///     producer is never invoked. Asserting the invocation count, not just the end state, is
    ///     what distinguishes "refused before scanning" from "scanned and happened to fail".
    /// </summary>
    [TestMethod]
    public async Task Mft_WithExistingOnlyCacheModeAndNoUsableCache_FailsTheRootWithoutInvokingTheProducer()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> CountingProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalIdentifier: 7, nextUsn: 4096, moment: FixedMoment), 0));
        }

        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), TreeRoot, 1),
            CacheDirectory = CacheDirectoryPath,
            MftSource = new MftIndexSource(CountingProducer),
            CacheMode = CacheMode.ExistingOnly
        }, TestContext.CancellationToken);

        Assert.AreEqual(0, invocationCount, "a cache-only open must never attempt a scan");
        var failed = index.Status;
        Assert.AreEqual(RootState.Failed, failed.State);
        Assert.AreEqual(
            "Root T: no usable cache (missing, corrupt, or incompatible) and CacheMode.ExistingOnly forbids a scan.",
            failed.Failure?.Message);
        Assert.AreEqual(RootFailureKind.CacheDeclined, failed.Failure?.Kind);
    }

    [TestMethod]
    public async Task Mft_StillPropagatesCancellation()
    {
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), TreeRoot, 1),
            CacheDirectory = CacheDirectoryPath,
            MftSource = new MftIndexSource(static (_, token) => throw new OperationCanceledException(token))
        }, TestContext.CancellationToken));
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

            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, 4096, FixedMoment), 2));
        }

        var options = Options(ProducerKind.Mft, Produce);
        await using (var index = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            await RootResultAssertions.FailureAsync<InvalidOperationException>(
                () => index.RescanAsync(CancellationToken.None));

            Assert.AreEqual(4096L, index.Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);
            Assert.AreEqual(RootState.Ready, index.Status.State);
            Assert.AreEqual(RootFailureKind.ProducerFailed, index.Status.Failure?.Kind);
            Assert.AreEqual("elevation declined during rescan", index.Status.Failure?.Message);
            Assert.AreEqual(BlockOrigin.ProducedByScan, index.Status.Block!.Origin);
            Assert.AreEqual(2, index.Status.Block!.SkippedRecordCount);
            Assert.AreEqual(0, index.Status.Block!.AccessDeniedSubtreeCount);
        }

        await using var reopened = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(4096L, reopened.Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);
        Assert.AreEqual(RootState.Ready, reopened.Status.State);
        Assert.AreEqual(0, reopened.Status.Block!.SkippedRecordCount);
        Assert.AreEqual(0, reopened.Status.Block!.AccessDeniedSubtreeCount);
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
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, nextUsn, FixedMoment), 0));
        }

        await using var index = await FileIndex.OpenAsync(Options(ProducerKind.Mft, Produce),
            CancellationToken.None);
        await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.RescanAsync(CancellationToken.None));
        Assert.AreEqual("elevation declined during rescan", index.Status.Failure?.Message);

        await index.RescanAsync(CancellationToken.None);

        Assert.IsNull(index.Status.Failure);
        Assert.AreEqual(12288L, index.Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);
    }

    [TestMethod]
    public async Task RescanAsync_Mft_InvokesProducerAgainAndAdoptsItsCursor()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken cancellationToken)
        {
            var nextUsn = 4096L * ++invocationCount;
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, nextUsn, FixedMoment), 0));
        }

        await using var index = await FileIndex.OpenAsync(Options(ProducerKind.Mft, Produce),
            CancellationToken.None);
        var previousRoot = index.Root(TestContext.CancellationToken);

        await index.RescanAsync(CancellationToken.None);

        Assert.AreEqual(2, invocationCount);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
        Assert.IsFalse(index.Status.Watch.IsSupported, "the source offers no watch source");
        Assert.AreEqual(8192L, index.Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);
        Assert.AreEqual(4096L, previousRoot.RootBlock.Block.Header.UsnNextUsn);
        Assert.AreEqual(TreeRoot, previousRoot.Path);
    }

    [TestMethod]
    public async Task OpenAsync_MftWithAFakeProducer_OpensAnMftProducedRoot()
    {
        Task<MftBlockProduceResult> FakeProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalIdentifier: 7, nextUsn: 4096, moment: FixedMoment), 0));
        }

        var options = Options(ProducerKind.Mft, FakeProducer);
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
        Assert.IsFalse(index.Status.Watch.IsSupported, "the source offers no watch source");
        Assert.AreEqual(RootState.Ready, index.Status.State);
    }

    [TestMethod]
    [DataRow("cache tag")]
    [DataRow("journal cursor")]
    public async Task OpenAsync_MftWithAnInvalidProducedBlock_DeletesItAndColdScansNextTime(string check)
    {
        BlockFile? producedBlock = null;
        Task<MftBlockProduceResult> InvalidProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to result, and the test asserts FileIndex disposes it on rejection
            var block = SeededBlocks.Build(request, 7, 4096, FixedMoment);
            // aislop-ignore-next-line dotnet/IDISP001 -- captures the instance to assert disposal on rejection
            producedBlock = block;
            if (check == "journal cursor")
            {
                return Task.FromResult(MftBlockValidation.Finish(request, block, new UsnJournalCursor(99, 12345), 0, null));
            }

            block.Header.CacheTagVersion++;
            return Task.FromResult(new MftBlockProduceResult(block, 0));
        }

        var options = Options(ProducerKind.Mft, InvalidProducer);

        await using (var failedIndex = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            var failedRoot = failedIndex.Status;
            Assert.AreEqual(RootState.Failed, failedRoot.State);
            Assert.AreEqual(RootFailureKind.ProducerFailed, failedRoot.Failure?.Kind);
            Assert.Contains(check, failedRoot.Failure?.Message!);
        }

        Assert.IsNotNull(producedBlock);
        BlockFileAssertions.IsDisposed(producedBlock);

        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('T'), 0x0BADF00D));
        Assert.IsFalse(File.Exists(blockPath),
            "a rejected block must not survive to be warm-started by a later open");

        var invocationCount = 0;
        Task<MftBlockProduceResult> CountingProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalIdentifier: 7, nextUsn: 4096, moment: FixedMoment), 0));
        }

        var recoveryOptions = Options(ProducerKind.Mft, CountingProducer);
        await using var index = await FileIndex.OpenAsync(recoveryOptions, CancellationToken.None);

        Assert.AreEqual(1, invocationCount,
            "the producer must run again rather than the index warm-starting from the rejected block");
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
    }


    /// <summary>
    ///     Close-record coalescing state is owned by the root block, so a rescan that
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
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, 7, nextUsn, FixedMoment), 0));
        }

        await using var index = await FileIndex.OpenAsync(Options(ProducerKind.Mft, Produce),
            TestContext.CancellationToken);
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
        Assert.HasCount(1, await index.ApplyJournalEntriesAsync('T', [controlCreated], 7, 5002));
        Assert.IsEmpty(await index.ApplyJournalEntriesAsync('T', [controlClosed], 7, 5003));

        // Record 20's create leaves its cycle open in ReportedReasonCycles leading up to rescan.
        Assert.HasCount(1, await index.ApplyJournalEntriesAsync('T', [created], 7, 5004));

        await index.RescanAsync(TestContext.CancellationToken);

        // On the replaced block, the open cycle was discarded with the old DriveBlock;
        // the close record is treated as a first-sighted create rather than coalesced.
        var afterRescan = await index.ApplyJournalEntriesAsync('T', [closed], 7, 8193);
        Assert.HasCount(1, afterRescan);
        Assert.AreEqual(FileChangeKind.Created, afterRescan[0].Kind);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedRoot_ScansItAndClearsTheFailureKind()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> Producer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalIdentifier: 7, nextUsn: 4096, moment: FixedMoment), 0));
        }

        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), TreeRoot, 0x0BADF00D),
            CacheDirectory = CacheDirectoryPath,
            MftSource = new MftIndexSource(Producer),
            CacheMode = CacheMode.ExistingOnly
        }, TestContext.CancellationToken);
        Assert.AreEqual(0, invocationCount, "a cache-only open must never attempt a scan");
        Assert.AreEqual(RootState.Failed, index.Status.State);
        Assert.AreEqual(RootFailureKind.CacheDeclined, index.Status.Failure?.Kind);

        await index.RescanAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, invocationCount);
        var status = index.Status;
        Assert.AreEqual(RootState.Ready, status.State);
        Assert.IsNull(status.Failure);
        Assert.IsNull(status.Failure);
        Assert.AreEqual(BlockOrigin.ProducedByScan, status.Block!.Origin);
        Assert.AreEqual(ProducerKind.Mft, index.HeaderOf().ProducerKind);
        Assert.IsFalse(status.Watch.IsSupported, "the source offers no watch source");
        Assert.AreEqual(4096L, index.Root(TestContext.CancellationToken).RootBlock.Block.Header.UsnNextUsn);
    }

    [TestMethod]
    public async Task RescanAsync_CacheDeclinedRootWhoseScanFails_StaysFailedAsProducerFailed()
    {
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), TreeRoot, 0x0BADF00D),
            CacheDirectory = CacheDirectoryPath,
            MftSource = new MftIndexSource((_, _) => throw new UnauthorizedAccessException("elevation declined")),
            CacheMode = CacheMode.ExistingOnly
        }, TestContext.CancellationToken);
        Assert.AreEqual(RootFailureKind.CacheDeclined, index.Status.Failure?.Kind);

        await RootResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.RescanAsync(TestContext.CancellationToken));

        var status = index.Status;
        Assert.AreEqual(RootState.Failed, status.State);
        Assert.AreEqual(RootFailureKind.ProducerFailed, status.Failure?.Kind);
        Assert.AreEqual("elevation declined", status.Failure?.Message);
    }

    [TestMethod]
    public async Task RescanAsync_ProducerFailedRoot_ScansItAndClearsTheFailureKind()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> Producer(MftBlockProduceRequest request, CancellationToken _)
        {
            if (++invocationCount == 1)
            {
                throw new UnauthorizedAccessException("elevation declined");
            }

            return Task.FromResult(new MftBlockProduceResult(SeededBlocks.Build(request, journalIdentifier: 7, nextUsn: 4096, moment: FixedMoment), 0));
        }

        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Root = MFTLibTestExtensions.SyntheticIndexedRoot.Create(new VolumeKey('T'), TreeRoot, 0x0BADF00D),
            CacheDirectory = CacheDirectoryPath,
            MftSource = new MftIndexSource(Producer)
        }, TestContext.CancellationToken);
        Assert.AreEqual(RootState.Failed, index.Status.State);
        Assert.AreEqual(RootFailureKind.ProducerFailed, index.Status.Failure?.Kind);

        await index.RescanAsync(TestContext.CancellationToken);

        Assert.AreEqual(2, invocationCount);
        var status = index.Status;
        Assert.AreEqual(RootState.Ready, status.State);
        Assert.IsNull(status.Failure);
        Assert.IsNull(status.Failure);
        Assert.AreEqual(BlockOrigin.ProducedByScan, status.Block!.Origin);
    }

    [TestMethod]
    public async Task OpenAsync_MftWithACursorMismatchedProducer_LogsTheRejectedBlocksDelete()
    {
        Task<MftBlockProduceResult> MismatchedProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to Finish, which disposes it on rejection
            var block = SeededBlocks.Build(request, journalIdentifier: 7, nextUsn: 4096, moment: FixedMoment);
            return Task.FromResult(MftBlockValidation.Finish(request, block, new UsnJournalCursor(99, 12345), 0, null));
        }

        var deletions = new List<string>();
        var options = Options(ProducerKind.Mft, MismatchedProducer) with { Diagnostics = deletions.Add };
        await using var failedIndex = await FileIndex.OpenAsync(options,
            TestContext.CancellationToken);

        Assert.AreEqual(RootState.Failed, failedIndex.Status.State);
        var blockPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('T'), 0x0BADF00D));
        var line = deletions.SingleOrDefault(entry => entry.Contains(blockPath));
        Assert.IsNotNull(line, "the rejected producer block's delete must be logged with its path");
        Assert.Contains("journal cursor", line);
    }
}
