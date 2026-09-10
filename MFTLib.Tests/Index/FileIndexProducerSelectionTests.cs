using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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

    string _treeRoot = null!;
    string _cacheDirectory = null!;

    public TestContext TestContext { get; set; } = null!;

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

    FileIndexOptions Options(ProducerPolicy producerPolicy, MftBlockProducer? mftProducer)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = producerPolicy,
            MftProducer = mftProducer
        };
    }

    /// <summary>
    ///     Writes a small, valid MFT-shaped block directly at <paramref name="request" />'s block
    ///     path (root row 5, matching <see cref="SyntheticBlockBuilder.MftShaped" />'s convention)
    ///     using the production <see cref="BlockWriter" /> rather than a copy of its logic, then
    ///     reopens it as a fresh handle, mirroring how a real producer's caller adopts the block
    ///     it wrote. The journal cursor is stamped through
    ///     <see cref="BlockWriter.SetJournalCursor" /> before <see cref="BlockWriter.Complete" />,
    ///     the same flush-safe order a real MFT producer follows, so the returned block's header
    ///     already carries the same cursor this fake reports back in its
    ///     <see cref="MftBlockProduceResult" />.
    /// </summary>
    static BlockFile BuildMftShapedBlock(MftBlockProduceRequest request, ulong journalId, long nextUsn)
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
            writer.SetJournalCursor(journalId, nextUsn);
            writer.Complete(FixedMoment);
        }

        return BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!;
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
            MftProducer = (request, _) => request.DriveLetter == 'T'
                ? throw new UnauthorizedAccessException("elevation declined")
                : Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, 7, 4096),
                    7, 4096, 0, false))
        }, TestContext.CancellationTokenSource.Token);

        var failed = index.Drives.Single(drive => drive.DriveLetter == 'T');
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual("elevation declined", failed.MftProducerFailureMessage);
        var ready = index.Drives.Single(drive => drive.DriveLetter == 'U');
        Assert.AreEqual(DriveState.Ready, ready.State);
        Assert.IsNull(ready.DiscardedBlock);
    }

    [TestMethod]
    public async Task Mft_FailedDriveDoesNotLeakItsFailureMessageToAWarmStartedDrive()
    {
        var firstRoot = Path.Combine(_treeRoot, "first");
        var secondRoot = Path.Combine(_treeRoot, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        Directory.CreateDirectory(_cacheDirectory);
        using (BuildMftShapedBlock(new MftBlockProduceRequest
        {
            DriveLetter = 'U',
            VolumeSerial = 2,
            BlockPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('U', 2))
        }, 7, 4096))
        {
        }

        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', firstRoot, 1), new IndexedDrive('U', secondRoot, 2)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = (_, _) => throw new UnauthorizedAccessException("elevation declined")
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
            MftProducer = (_, _) => throw new UnauthorizedAccessException("elevation declined")
        }, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(DriveState.Failed, index.Drives.Single().State);
        Assert.AreEqual(0, index.Search(new SearchQuery(null)).Count);
    }

    [TestMethod]
    public async Task Mft_WithNoProducerConfiguredIsAConfigurationError()
    {
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft
        }, TestContext.CancellationTokenSource.Token));
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
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "indexed.txt"), "x",
            TestContext.CancellationTokenSource.Token);
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration,
            MftProducer = (_, _) => throw new InvalidOperationException("the MFT producer must not be called")
        }, TestContext.CancellationTokenSource.Token);

        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(ProducerKind.Enumeration, status.ProducerKind);
        Assert.IsNull(status.MftProducerFailureMessage);
        Assert.IsNotNull(index.Find(@"T:\indexed.txt"));
    }

    [TestMethod]
    public async Task Mft_StillPropagatesCancellation()
    {
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => FileIndex.OpenAsync(new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 1)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Mft,
            MftProducer = static (_, token) => throw new OperationCanceledException(token)
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

            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, 7, 4096),
                7, 4096, 0, false));
        }

        var options = Options(ProducerPolicy.Mft, Produce);
        await using (var index = await FileIndex.OpenAsync(options, CancellationToken.None))
        {
            await index.RescanAsync('T', CancellationToken.None);

            Assert.AreEqual(4096L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
            Assert.AreEqual("elevation declined during rescan", index.Drives.Single().MftProducerFailureMessage);
        }

        await using var reopened = await FileIndex.OpenAsync(options, CancellationToken.None);
        Assert.AreEqual(4096L, reopened.Root('T').DriveBlock.Block.Header.UsnNextUsn);
        Assert.AreEqual(DriveState.Ready, reopened.Drives.Single().State);
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
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, 7, nextUsn),
                7, nextUsn, 0, false));
        }

        await using var index = await FileIndex.OpenAsync(Options(ProducerPolicy.Mft, Produce),
            CancellationToken.None);
        await index.RescanAsync('T', CancellationToken.None);
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
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, 7, nextUsn),
                7, nextUsn, 0, false));
        }

        await using var index = await FileIndex.OpenAsync(Options(ProducerPolicy.Mft, Produce),
            CancellationToken.None);
        var previousRoot = index.Root('T');

        await index.RescanAsync('T', CancellationToken.None);

        Assert.AreEqual(2, invocationCount);
        Assert.AreEqual(ProducerKind.Mft, index.Drives[0].ProducerKind);
        Assert.IsTrue(index.Drives[0].WatchSupported);
        Assert.AreEqual(8192L, index.Root('T').DriveBlock.Block.Header.UsnNextUsn);
        Assert.AreEqual(4096L, previousRoot.DriveBlock.Block.Header.UsnNextUsn);
        Assert.AreEqual(@"T:\", previousRoot.Path);
    }

    [TestMethod]
    public async Task OpenAsync_MftWithAFakeProducer_OpensAnMftProducedDriveWithWatchSupport()
    {
        Task<MftBlockProduceResult> FakeProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false));
        }

        var options = Options(ProducerPolicy.Mft, FakeProducer);
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(ProducerKind.Mft, index.Drives[0].ProducerKind);
        Assert.IsTrue(index.Drives[0].WatchSupported);
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
    }

    [TestMethod]
    public async Task OpenAsync_MftWithACursorMismatchedProducer_DeletesTheRejectedBlockSoALaterOpenDoesNotWarmStartFromIt()
    {
        Task<MftBlockProduceResult> MismatchedProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 99, NextUsn: 12345, SkippedRecordCount: 0, CompactionNeeded: false));
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
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false));
        }

        var recoveryOptions = Options(ProducerPolicy.Mft, CountingProducer);
        await using var index = await FileIndex.OpenAsync(recoveryOptions, CancellationToken.None);

        Assert.AreEqual(1, invocationCount,
            "the producer must run again rather than the index warm-starting from the rejected block");
        Assert.AreEqual(ProducerKind.Mft, index.Drives[0].ProducerKind);
    }

    [TestMethod]
    public async Task OpenAsync_EnumerationWithAFakeProducer_IgnoresIt()
    {
        var invocationCount = 0;

        Task<MftBlockProduceResult> FakeProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false));
        }

        var options = Options(ProducerPolicy.Enumeration, FakeProducer);
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(0, invocationCount);
        Assert.AreEqual(ProducerKind.Enumeration, index.Drives[0].ProducerKind);
    }
}
