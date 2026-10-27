using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

public partial class FileIndexPerDriveProducerKindTests
{
    [TestMethod]
    [DataRow(CacheMode.Persistent)]
    [DataRow(CacheMode.Disabled)]
    public async Task MixedOpen_EnumerationFailureSettlesWithoutLosingHealthyMftSibling(CacheMode cacheMode)
    {
        EnumerationWalkTestSupport.GenerateBranchingTree(Fixture.TreeRoot, 2, 2, 2);
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var failure = new IOException("enumeration progress failed");
        var progress = new SynchronousProgress<IndexScanProgress>(sample =>
        {
            samples.Enqueue(sample);
            if (sample.Drive == new VolumeKey('U') && sample.Phase != IndexScanPhase.Finished)
            {
                throw failure;
            }
        });
        var optionsT = Options(Drive('T', ProducerKind.Mft)) with
        {
            CacheMode = cacheMode,
            ScanProgress = progress
        };
        var optionsU = Options(Drive('U', ProducerKind.Enumeration)) with
        {
            CacheMode = cacheMode,
            ScanProgress = progress
        };

        await using var indexT = await FileIndex.OpenAsync(optionsT, CancellationToken.None);
        await using var indexU = await FileIndex.OpenAsync(optionsU, CancellationToken.None);

        AssertDrive(indexT, 'T', ProducerKind.Mft);
        Assert.AreEqual(new VolumeKey('T'), indexT.Search(new SearchQuery("scanned.txt"), TestContext.CancellationToken).Single().RecordKey.Drive);
        var failed = indexU.Status;
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, failed.Failure?.Kind);
        Assert.AreEqual(failure.Message, failed.Failure?.Message);
        Assert.IsNull(failed.Block);
        Assert.AreEqual(IndexScanOutcome.Failed,
            samples.Single(sample => sample.Drive == new VolumeKey('U') && sample.Phase == IndexScanPhase.Finished).Outcome);
        Assert.IsFalse(File.Exists(Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('U'), 123))));
    }

    [TestMethod]
    [DataRow(CacheMode.Persistent)]
    [DataRow(CacheMode.Disabled)]
    public async Task EnumerationRescan_ProducerFailurePreservesOldBlockAndRecordsFailure(CacheMode cacheMode)
    {
        EnumerationWalkTestSupport.GenerateBranchingTree(Fixture.TreeRoot, 2, 2, 2);
        var failure = new IOException("enumeration rescan failed");
        var progress = new FailingScanProgress();
        var options = Options(Drive('T', ProducerKind.Enumeration)) with
        {
            CacheMode = cacheMode,
            ScanProgress = progress
        };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        var previousBlock = index.CurrentBlock!.Block;
        var previousStatus = index.Status.Block;
        var canonicalPath = Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('T'), 123));
        var previousBytes = cacheMode == CacheMode.Persistent ? ReadSharedBlock(canonicalPath) : null;

        progress.Failure = failure;
        var reported = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.RescanAsync(CancellationToken.None));

        Assert.AreSame(failure, reported.InnerException);
        Assert.AreSame(previousBlock, index.CurrentBlock!.Block);
        var status = index.Status;
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(previousStatus, status.Block);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.Failure?.Kind);
        Assert.AreEqual(failure.Message, status.Failure?.Message);
        Assert.AreEqual("walked.txt", index.Search(new SearchQuery("walked.txt"), TestContext.CancellationToken).Single().Name);
        if (previousBytes is not null)
        {
            Assert.AreSequenceEqual(previousBytes, ReadSharedBlock(canonicalPath));
            Assert.IsEmpty(Directory.GetFiles(CacheDirectoryPath, "*.retired-*"));
        }

        progress.Failure = null;
        var recovered = await index.RescanAsync(CancellationToken.None);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, recovered.Outcome);
        Assert.IsNull(index.Status.Failure);
    }

    [TestMethod]
    [DataRow(CacheMode.Persistent)]
    [DataRow(CacheMode.Disabled)]
    public async Task Open_EnumerationCancellationReleasesNoBlock(CacheMode cacheMode)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var options = Options(Drive('U', ProducerKind.Enumeration)) with
        {
            CacheMode = cacheMode,
            ScanProgress = new CancelEnumerationProgress(cancellation)
        };

        var thrown = await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await using var unexpected = await FileIndex.OpenAsync(options, token);
        });

        Assert.AreEqual(token, thrown.CancellationToken);
        Assert.IsFalse(File.Exists(Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('U'), 123))));
        await using var reopened = await FileIndex.OpenAsync(options with
        {
            ScanProgress = null
        }, CancellationToken.None);
        AssertDrive(reopened, 'U', ProducerKind.Enumeration);
        Assert.AreEqual(cacheMode == CacheMode.Persistent ? CacheLocation.OwnedCanonical : null,
            reopened.Status.Block?.CacheLocation);
    }

    static byte[] ReadSharedBlock(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var contents = new MemoryStream();
        stream.CopyTo(contents);
        return contents.ToArray();
    }

    sealed class FailingScanProgress : IProgress<IndexScanProgress>
    {
        public Exception? Failure { get; set; }

        public void Report(IndexScanProgress value)
        {
            if (value.Phase != IndexScanPhase.Finished && Failure is { } failure)
            {
                throw failure;
            }
        }
    }

    sealed class CancelEnumerationProgress(CancellationTokenSource cancellation)
        : IProgress<IndexScanProgress>
    {
        public void Report(IndexScanProgress value)
        {
            if (value.Drive == new VolumeKey('U') && value.Phase != IndexScanPhase.Finished)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        }
    }
}
