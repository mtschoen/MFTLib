using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     A cancelled or failed scan into the canonical cache slot deletes the partial block it wrote,
///     while its index still holds the block's owner lock, and reports the delete through
///     <see cref="FileIndexOptions.Diagnostics" />. A rescan's previous block survives.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FileIndexPartialCanonicalBlockTests
{
    public TestContext TestContext { get; set; } = null!;

    CancellationToken Token => TestContext.CancellationTokenSource.Token;

    const uint VolumeSerial = 0x2970;
    OwnedIndexDirectories _directories = null!;
    string _treeRoot = null!;
    string _cacheDirectory = null!;
    string _canonicalPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directories = new OwnedIndexDirectories();
        _treeRoot = _directories.TreeRoot;
        _cacheDirectory = _directories.CacheDirectory;
        Directory.CreateDirectory(_treeRoot);
        Directory.CreateDirectory(_cacheDirectory);
        _canonicalPath = Path.Combine(_cacheDirectory, CacheDirectory.BlockFileName('T', VolumeSerial));
    }

    [TestCleanup]
    public void Cleanup()
    {
        _directories.Dispose();
    }

    FileIndexOptions Options(MftBlockProducer producer, List<string> diagnostics, bool cacheOnly = false) => new()
    {
        Drives = [new IndexedDrive('T', _treeRoot, VolumeSerial)],
        CacheDirectory = _cacheDirectory,
        ProducerPolicy = ProducerPolicy.Mft,
        MftSource = new MftIndexSource(producer),
        InitialOpenCacheOnly = cacheOnly,
        Diagnostics = diagnostics.Add
    };

    /// <summary>Leaves a half-written canonical file behind, as a producer killed mid-scan would.</summary>
    static void WritePartialFile(MftBlockProduceRequest request) =>
        File.WriteAllBytes(request.BlockPath, [1, 2, 3]);

    static MftBlockProduceResult CompleteBlock(MftBlockProduceRequest request)
    {
        SeededBlocks.Write(request.BlockPath, request.VolumeSerial, journalId: 7, nextUsn: 4096, moment: SeededBlocks.SeededMoment);
        return new MftBlockProduceResult(BlockFile.Open(request.BlockPath, request.VolumeSerial, out _)!,
            JournalId: 7, NextUsn: 4096, SkippedRecordCount: 0);
    }

    void AssertPartialDeleteLogged(List<string> diagnostics)
    {
        var line = diagnostics.SingleOrDefault(entry => entry.Contains(_canonicalPath) && entry.Contains("partial"));
        Assert.IsNotNull(line, "the partial block's delete is reported with its path and reason");
        Assert.IsTrue(line.StartsWith("Deleted block file", StringComparison.Ordinal), line);
    }

    [TestMethod]
    public async Task OpenAsync_EnumerationScanCancelledMidScan_DeletesThePartialCanonicalBlock()
    {
        Directory.CreateDirectory(Path.Combine(_treeRoot, "Documents"));
        await File.WriteAllTextAsync(Path.Combine(_treeRoot, "Documents", "readme.md"), "hello", Token);
        using var cancellation = new CancellationTokenSource();
        var diagnostics = new List<string>();
        var options = new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, VolumeSerial)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration,
            Progress = new CancelingProgress(cancellation),
            Diagnostics = diagnostics.Add
        };

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => FileIndex.OpenAsync(options, cancellation.Token));

        Assert.IsFalse(File.Exists(_canonicalPath), "the cancelled cold scan leaves no partial canonical block");
        AssertPartialDeleteLogged(diagnostics);
    }

    [TestMethod]
    public async Task OpenAsync_MftProducerCancelled_DeletesThePartialCanonicalBlock()
    {
        var diagnostics = new List<string>();
        var options = Options((request, _) =>
        {
            WritePartialFile(request);
            throw new OperationCanceledException();
        }, diagnostics);

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => FileIndex.OpenAsync(options, Token));

        Assert.IsFalse(File.Exists(_canonicalPath));
        AssertPartialDeleteLogged(diagnostics);
    }

    [TestMethod]
    public async Task OpenAsync_MftProducerFails_DeletesThePartialCanonicalBlockAndKeepsTheFailure()
    {
        var diagnostics = new List<string>();
        var options = Options((request, _) =>
        {
            WritePartialFile(request);
            throw new IOException("the producer lost the volume");
        }, diagnostics);

        await using var index = await FileIndex.OpenAsync(options, Token);

        var drive = index.Drives.Single();
        Assert.AreEqual(DriveState.Failed, drive.State);
        Assert.AreEqual("the producer lost the volume", drive.MftProducerFailureMessage);
        Assert.IsFalse(File.Exists(_canonicalPath));
        AssertPartialDeleteLogged(diagnostics);
    }

    [TestMethod]
    public async Task OpenAsync_MftProducerFailsBeforeCreatingAFile_LogsNoDelete()
    {
        var diagnostics = new List<string>();
        var options = Options((_, _) => throw new IOException("nothing was written"), diagnostics);

        await using var index = await FileIndex.OpenAsync(options, Token);

        Assert.AreEqual(DriveState.Failed, index.Drives.Single().State);
        Assert.AreEqual(0, diagnostics.Count(entry => entry.Contains(_canonicalPath)),
            "no file existed, so no delete is reported");
    }

    [TestMethod]
    public async Task OpenAsync_PartialCanonicalBlockThatCannotBeDeleted_ReportsItAndKeepsTheOriginalFailure()
    {
        var diagnostics = new List<string>();
        var options = Options((request, _) =>
        {
            // File.Delete refuses a directory on every platform, standing in for a locked file.
            Directory.CreateDirectory(request.BlockPath);
            throw new IOException("the producer lost the volume");
        }, diagnostics);

        await using var index = await FileIndex.OpenAsync(options, Token);

        Assert.AreEqual("the producer lost the volume", index.Drives.Single().MftProducerFailureMessage);
        var line = diagnostics.SingleOrDefault(entry => entry.Contains(_canonicalPath));
        Assert.IsNotNull(line, "the failed delete is reported through Diagnostics");
        Assert.IsTrue(line.StartsWith("Could not delete block file", StringComparison.Ordinal), line);
    }

    [TestMethod]
    public async Task RescanAsync_OfABlocklessDriveWhoseProducerFails_DeletesThePartialCanonicalBlock()
    {
        var diagnostics = new List<string>();
        var options = Options((request, _) =>
        {
            WritePartialFile(request);
            throw new IOException("the producer lost the volume");
        }, diagnostics, cacheOnly: true);
        await using var index = await FileIndex.OpenAsync(options, Token);
        Assert.AreEqual(DriveState.Failed, index.Drives.Single().State);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => index.RescanAsync('T', Token));

        Assert.IsFalse(File.Exists(_canonicalPath));
        AssertPartialDeleteLogged(diagnostics);
    }

    [TestMethod]
    public async Task RescanAsync_OfABlocklessDriveCancelled_DeletesThePartialCanonicalBlock()
    {
        var diagnostics = new List<string>();
        var options = Options((request, _) =>
        {
            WritePartialFile(request);
            throw new OperationCanceledException();
        }, diagnostics, cacheOnly: true);
        await using var index = await FileIndex.OpenAsync(options, Token);

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => index.RescanAsync('T', Token));

        Assert.IsFalse(File.Exists(_canonicalPath));
        AssertPartialDeleteLogged(diagnostics);
    }

    [TestMethod]
    public async Task RescanAsync_CancelledOverAPublishedBlock_KeepsThePreviousBlockAndItsFile()
    {
        var diagnostics = new List<string>();
        var scans = 0;
        var options = Options((request, _) =>
        {
            if (++scans == 1)
            {
                return Task.FromResult(CompleteBlock(request));
            }

            WritePartialFile(request);
            throw new OperationCanceledException();
        }, diagnostics);
        await using var index = await FileIndex.OpenAsync(options, Token);
        var rowCount = index.HeaderOf().RowCount;

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => index.RescanAsync('T', Token));

        Assert.AreEqual(DriveState.Ready, index.Drives.Single().State);
        Assert.AreEqual(rowCount, index.HeaderOf().RowCount);
        Assert.IsTrue(new FileInfo(_canonicalPath).Length > 3,
            "the slot holds the complete previous block, not the partial replacement");
        Assert.AreEqual(0, Directory.GetFiles(_cacheDirectory, "*.retired-*").Length);
    }

    [TestMethod]
    public async Task OpenAsync_FailedScanWithoutDiagnostics_StillDeletesOrToleratesThePartialBlock()
    {
        var deletable = new System.Runtime.CompilerServices.StrongBox<bool>(true);
        var options = Options((request, _) =>
        {
            if (deletable.Value)
            {
                WritePartialFile(request);
            }
            else
            {
                Directory.CreateDirectory(request.BlockPath);
            }

            throw new IOException("the producer lost the volume");
        }, []) with
        { Diagnostics = null };

        await using (var index = await FileIndex.OpenAsync(options, Token))
        {
            Assert.AreEqual(DriveState.Failed, index.Drives.Single().State);
        }

        Assert.IsFalse(File.Exists(_canonicalPath));

        deletable.Value = false;
        await using var second = await FileIndex.OpenAsync(options, Token);
        Assert.AreEqual("the producer lost the volume", second.Drives.Single().MftProducerFailureMessage,
            "a delete that fails with no Diagnostics callback still leaves the scan's failure intact");
    }

    [TestMethod]
    public async Task OpenAsync_ThrowingDiagnosticsAfterASuccessfulDelete_KeepsTheProducersFailure()
    {
        var options = Options((request, _) =>
        {
            WritePartialFile(request);
            throw new IOException("the producer lost the volume");
        }, []) with
        { Diagnostics = _ => throw new IOException("the log destination closed") };

        await using var index = await FileIndex.OpenAsync(options, Token);

        Assert.AreEqual("the producer lost the volume", index.Drives.Single().MftProducerFailureMessage);
        Assert.IsFalse(File.Exists(_canonicalPath));
    }

    [TestMethod]
    public async Task OpenAsync_ThrowingDiagnosticsAfterAFailedDelete_KeepsTheProducersCancellation()
    {
        var options = Options((request, _) =>
        {
            Directory.CreateDirectory(request.BlockPath);
            throw new OperationCanceledException();
        }, []) with
        { Diagnostics = _ => throw new InvalidOperationException("the log destination closed") };

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => FileIndex.OpenAsync(options, Token));
    }

    /// <summary>
    ///     Seeds a complete cached block, then opens again with a journal that no longer holds its
    ///     checkpoint so the block is rejected without being deleted and the scan's producer runs
    ///     over the still-present file. Returns the seeded bytes.
    /// </summary>
    async Task<byte[]> OpenOverUnresumableCacheAsync(Func<MftBlockProduceRequest, Exception> scanFailure,
        List<string> diagnostics, Func<Task<FileIndex>, Task> assertOutcome)
    {
        var failTheScan = new System.Runtime.CompilerServices.StrongBox<bool>(false);
        var options = Options((request, _) => failTheScan.Value
            ? throw scanFailure(request)
            : Task.FromResult(CompleteBlock(request)), diagnostics);
        using (JournalCheckpointCheck.OverrideJournalForTest(
                   _ => new JournalWindow(7, 0, 4096, 64, 128L * 1024 * 1024)))
        {
            await using var seeded = await FileIndex.OpenAsync(options, Token);
        }

        var seededBytes = await File.ReadAllBytesAsync(_canonicalPath, Token);
        failTheScan.Value = true;
        using (JournalCheckpointCheck.OverrideJournalForTest(
                   _ => new JournalWindow(7, 10_000, 20_000, 64, 128L * 1024 * 1024)))
        {
            await assertOutcome(FileIndex.OpenAsync(options, Token));
        }

        return seededBytes;
    }

    [TestMethod]
    public async Task OpenAsync_UnresumableCachedBlockAndAProducerThatFailsBeforeWriting_KeepsTheCachedFile()
    {
        var diagnostics = new List<string>();
        var seededBytes = await OpenOverUnresumableCacheAsync(_ => new IOException("the broker could not connect"),
            diagnostics, async opening =>
            {
                await using var index = await opening;
                Assert.AreEqual(DriveState.Failed, index.Drives.Single().State);
            });

        CollectionAssert.AreEqual(seededBytes, await File.ReadAllBytesAsync(_canonicalPath, Token),
            "a scan that never wrote leaves the cached block it did not touch");
        Assert.AreEqual(0, diagnostics.Count(entry => entry.Contains("partial")));
    }

    [TestMethod]
    public async Task OpenAsync_UnresumableCachedBlockOverwrittenThenTheProducerFails_DeletesThePartialReplacement()
    {
        var diagnostics = new List<string>();
        await OpenOverUnresumableCacheAsync(request =>
            {
                WritePartialFile(request);
                return new IOException("the scan failed after truncating the cache");
            }, diagnostics, async opening =>
            {
                await using var index = await opening;
                Assert.AreEqual(DriveState.Failed, index.Drives.Single().State);
            });

        Assert.IsFalse(File.Exists(_canonicalPath), "the failed scan's partial replacement is deleted");
        AssertPartialDeleteLogged(diagnostics);
    }

    [TestMethod]
    public async Task OpenAsync_UnresumableCachedBlockOverwrittenThenTheProducerIsCancelled_DeletesThePartialReplacement()
    {
        var diagnostics = new List<string>();
        await OpenOverUnresumableCacheAsync(request =>
            {
                WritePartialFile(request);
                return new OperationCanceledException();
            }, diagnostics,
            opening => Assert.ThrowsExceptionAsync<OperationCanceledException>(() => opening));

        Assert.IsFalse(File.Exists(_canonicalPath), "the cancelled scan's partial replacement is deleted");
        AssertPartialDeleteLogged(diagnostics);
    }

    sealed class CancelingProgress(CancellationTokenSource cancellation) : IProgress<IndexScanProgress>
    {
        public void Report(IndexScanProgress value) => cancellation.Cancel();
    }
}
