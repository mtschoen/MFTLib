using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     How <see cref="FileIndex.OpenAsync" /> selects between the MFT producer and the
///     enumeration producer under each <see cref="ProducerPolicy" />: <see cref="ProducerPolicy.MftOnly" />
///     requires <see cref="FileIndexOptions.MftProducer" /> and lets a producer failure propagate,
///     <see cref="ProducerPolicy.Auto" /> prefers the MFT producer but falls back to enumeration
///     when it throws, and <see cref="ProducerPolicy.EnumerationOnly" /> ignores the MFT producer
///     entirely. Every fake producer writes a small MFT-shaped block directly through
///     <see cref="BlockFile" /> and <see cref="BlockWriter" />, so these tests run on Linux too.
/// </summary>
[TestClass]
public class FileIndexProducerSelectionTests
{
    static readonly DateTime FixedMoment = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

    string _treeRoot = null!;
    string _cacheDirectory = null!;

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
                    ModifiedTicks: FixedMoment.Ticks));
            writer.TryWriteRow(5, ".",
                new RowColumns(ParentRow: 5, Flags: RowFlags.InUse | RowFlags.Directory, Attributes: 0, Size: 0,
                    ModifiedTicks: FixedMoment.Ticks));
            writer.SetJournalCursor(journalId, nextUsn);
            writer.Complete(FixedMoment);
        }

        return BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!;
    }

    [TestMethod]
    public async Task RescanAsync_MftOnly_InvokesProducerAgainAndAdoptsItsCursor()
    {
        var invocationCount = 0;
        Task<MftBlockProduceResult> Produce(MftBlockProduceRequest request, CancellationToken cancellationToken)
        {
            var nextUsn = 4096L * ++invocationCount;
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, 7, nextUsn),
                7, nextUsn, 0, false));
        }

        await using var index = await FileIndex.OpenAsync(Options(ProducerPolicy.MftOnly, Produce),
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
    public async Task OpenAsync_MftOnlyWithNoProducer_ThrowsAndNamesTheOption()
    {
        var options = Options(ProducerPolicy.MftOnly, mftProducer: null);

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => FileIndex.OpenAsync(options, CancellationToken.None));

        StringAssert.Contains(exception.Message, nameof(FileIndexOptions.MftProducer));
    }

    [TestMethod]
    public async Task OpenAsync_MftOnlyWithAFakeProducer_OpensAnMftProducedDriveWithWatchSupport()
    {
        Task<MftBlockProduceResult> FakeProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false));
        }

        var options = Options(ProducerPolicy.MftOnly, FakeProducer);
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(ProducerKind.Mft, index.Drives[0].ProducerKind);
        Assert.IsTrue(index.Drives[0].WatchSupported);
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
    }

    [TestMethod]
    public async Task OpenAsync_AutoWithAFakeProducer_PrefersItOverEnumeration()
    {
        var invocationCount = 0;

        Task<MftBlockProduceResult> FakeProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false));
        }

        var options = Options(ProducerPolicy.Auto, FakeProducer);
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(1, invocationCount);
        Assert.AreEqual(ProducerKind.Mft, index.Drives[0].ProducerKind);
        Assert.IsTrue(index.Drives[0].WatchSupported);
    }

    [TestMethod]
    public async Task OpenAsync_AutoWithAThrowingProducer_FallsBackToEnumerationAndStillOpens()
    {
        var invocationCount = 0;

        Task<MftBlockProduceResult> ThrowingProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            throw new InvalidOperationException("synthetic MFT producer failure for the fallback test");
        }

        var options = Options(ProducerPolicy.Auto, ThrowingProducer);
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(1, invocationCount);
        Assert.AreEqual(ProducerKind.Enumeration, index.Drives[0].ProducerKind);
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        Assert.IsFalse(index.Drives[0].WatchSupported);
        StringAssert.Contains(index.Drives[0].MftProducerFailureMessage, "synthetic MFT producer failure");
    }

    [TestMethod]
    public async Task OpenAsync_AutoWithAProducerWhoseCursorDoesNotMatchItsBlockHeader_FallsBackToEnumerationAndRecordsIt()
    {
        // The block's own header carries cursor (7, 4096), but the producer reports a different
        // cursor in its result. FileIndex must not trust a producer that contradicts itself.
        Task<MftBlockProduceResult> MismatchedProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 99, NextUsn: 12345, SkippedRecordCount: 0, CompactionNeeded: false));
        }

        var options = Options(ProducerPolicy.Auto, MismatchedProducer);
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(ProducerKind.Enumeration, index.Drives[0].ProducerKind);
        Assert.AreEqual(DriveState.Ready, index.Drives[0].State);
        StringAssert.Contains(index.Drives[0].MftProducerFailureMessage, "journal cursor");
    }

    [TestMethod]
    public async Task RescanAsync_AutoWithAProducerThatRecovers_ClearsThePreviouslyRecordedFailureMessage()
    {
        var hasFailedOnce = false;

        Task<MftBlockProduceResult> ProducerThatFailsOnce(MftBlockProduceRequest request,
            CancellationToken cancellationToken)
        {
            if (!hasFailedOnce)
            {
                hasFailedOnce = true;
                throw new InvalidOperationException("synthetic MFT producer failure for the recovery test");
            }

            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false));
        }

        var options = Options(ProducerPolicy.Auto, ProducerThatFailsOnce);
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        StringAssert.Contains(index.Drives[0].MftProducerFailureMessage, "synthetic MFT producer failure");

        await index.RescanAsync('T', CancellationToken.None);

        Assert.AreEqual(ProducerKind.Mft, index.Drives[0].ProducerKind);
        Assert.IsTrue(index.Drives[0].WatchSupported);
        Assert.IsNull(index.Drives[0].MftProducerFailureMessage,
            "a drive that has since recovered must not keep reporting a stale producer failure");
    }

    [TestMethod]
    public async Task OpenAsync_MftOnlyWithACursorMismatchedProducer_DeletesTheRejectedBlockSoALaterOpenDoesNotWarmStartFromIt()
    {
        Task<MftBlockProduceResult> MismatchedProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 99, NextUsn: 12345, SkippedRecordCount: 0, CompactionNeeded: false));
        }

        var options = Options(ProducerPolicy.MftOnly, MismatchedProducer);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => FileIndex.OpenAsync(options, CancellationToken.None));

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

        var recoveryOptions = Options(ProducerPolicy.MftOnly, CountingProducer);
        await using var index = await FileIndex.OpenAsync(recoveryOptions, CancellationToken.None);

        Assert.AreEqual(1, invocationCount,
            "the producer must run again rather than the index warm-starting from the rejected block");
        Assert.AreEqual(ProducerKind.Mft, index.Drives[0].ProducerKind);
    }

    [TestMethod]
    public async Task OpenAsync_EnumerationOnlyWithAFakeProducer_IgnoresIt()
    {
        var invocationCount = 0;

        Task<MftBlockProduceResult> FakeProducer(MftBlockProduceRequest request, CancellationToken _)
        {
            invocationCount++;
            return Task.FromResult(new MftBlockProduceResult(BuildMftShapedBlock(request, journalId: 7, nextUsn: 4096),
                JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0, CompactionNeeded: false));
        }

        var options = Options(ProducerPolicy.EnumerationOnly, FakeProducer);
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        Assert.AreEqual(0, invocationCount);
        Assert.AreEqual(ProducerKind.Enumeration, index.Drives[0].ProducerKind);
    }
}
