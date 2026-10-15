using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

public partial class FileIndexPerDriveProducerKindTests
{
    [TestMethod]
    [DataRow(CacheMode.Persistent)]
    [DataRow(CacheMode.Disabled)]
    public async Task MixedOpen_EnumerationFailureSettlesWithoutLosingHealthyMftSibling(CacheMode cacheMode)
    {
        var samples = new ConcurrentQueue<IndexScanProgress>();
        var failure = new IOException("enumeration progress failed");
        var options = Options(Drive('T', ProducerKind.Mft), Drive('U', ProducerKind.Enumeration)) with
        {
            CacheMode = cacheMode,
            ScanProgress = new SynchronousProgress<IndexScanProgress>(sample =>
            {
                samples.Enqueue(sample);
                if (sample.Drive == 'U' && sample.Phase != IndexScanPhase.Finished)
                {
                    throw failure;
                }
            })
        };

        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);

        AssertDrive(index, 'T', ProducerKind.Mft);
        Assert.AreEqual(new DriveKey('T'), index.Search(new SearchQuery("scanned.txt")).Single().RecordKey.Drive);
        var failed = index.Drives.Single(drive => drive.Drive == 'U');
        Assert.AreEqual(DriveState.Failed, failed.State);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, failed.Failure?.Kind);
        Assert.AreEqual(failure.Message, failed.Failure?.Message);
        Assert.IsNull(failed.Block);
        Assert.AreEqual(IndexScanOutcome.Failed,
            samples.Single(sample => sample.Drive == 'U' && sample.Phase == IndexScanPhase.Finished).Outcome);
        Assert.IsFalse(File.Exists(Path.Combine(_directories.CacheDirectory, CacheDirectory.BlockFileName('U', 123))));
    }

    [TestMethod]
    [DataRow(CacheMode.Persistent)]
    [DataRow(CacheMode.Disabled)]
    public async Task EnumerationRescan_ProducerFailurePreservesOldBlockAndRecordsFailure(CacheMode cacheMode)
    {
        var failure = new IOException("enumeration rescan failed");
        var progress = new FailingScanProgress();
        var options = Options(Drive('T', ProducerKind.Enumeration)) with
        {
            CacheMode = cacheMode,
            ScanProgress = progress
        };
        await using var index = await FileIndex.OpenAsync(options, CancellationToken.None);
        var previousBlock = index.CurrentSnapshot.DriveBlocks.Single().Block;
        var previousStatus = index.Drives.Single().Block;
        var canonicalPath = Path.Combine(_directories.CacheDirectory, CacheDirectory.BlockFileName('T', 123));
        var previousBytes = cacheMode == CacheMode.Persistent ? ReadSharedBlock(canonicalPath) : null;

        progress.Failure = failure;
        var reported = await DriveResultAssertions.FailureAsync<InvalidOperationException>(
            () => index.RescanAsync(['T'], CancellationToken.None));

        Assert.AreSame(failure, reported.InnerException);
        Assert.AreSame(previousBlock, index.CurrentSnapshot.DriveBlocks.Single().Block);
        var status = index.Drives.Single();
        Assert.AreEqual(DriveState.Ready, status.State);
        Assert.AreEqual(previousStatus, status.Block);
        Assert.AreEqual(DriveFailureKind.ProducerFailed, status.Failure?.Kind);
        Assert.AreEqual(failure.Message, status.Failure?.Message);
        Assert.AreEqual("walked.txt", index.Search(new SearchQuery("walked.txt")).Single().Name);
        if (previousBytes is not null)
        {
            CollectionAssert.AreEqual(previousBytes, ReadSharedBlock(canonicalPath));
            Assert.AreEqual(0, Directory.GetFiles(_directories.CacheDirectory, "*.retired-*").Length);
        }

        progress.Failure = null;
        var recovered = await index.RescanAsync(['T'], CancellationToken.None);
        Assert.AreEqual(DriveOperationOutcome.Succeeded, recovered.Single().Outcome);
        Assert.IsNull(index.Drives.Single().Failure);
    }

    [TestMethod]
    [DataRow(CacheMode.Persistent)]
    [DataRow(CacheMode.Disabled)]
    public async Task MixedOpen_EnumerationCancellationReleasesAlreadyAdoptedSibling(CacheMode cacheMode)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var siblingSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockFile? siblingBlock = null;
        string? siblingPath = null;
        var producer = Producer();
        var options = Options(Drive('T', ProducerKind.Mft), Drive('U', ProducerKind.Enumeration)) with
        {
            CacheMode = cacheMode,
            MftSource = new MftIndexSource(async (request, producerToken) =>
            {
                var produced = await producer.Producer(request, producerToken);
                siblingBlock = produced.Block;
                siblingPath = request.BlockPath;
                return produced;
            }),
            DriveOpenProgress = new SynchronousProgress<IndexDriveOpened>(opened =>
            {
                if (opened.Drive == 'T')
                {
                    siblingSettled.TrySetResult();
                }
            }),
            ScanProgress = new CancelEnumerationAfterSiblingSettles(cancellation, siblingSettled.Task)
        };

        var thrown = await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
        {
            await using var unexpected = await FileIndex.OpenAsync(options, token);
        });

        Assert.AreEqual(token, thrown.CancellationToken);
        Assert.IsNotNull(siblingBlock);
        Assert.IsNotNull(siblingPath);
        Assert.ThrowsException<ObjectDisposedException>(() => _ = siblingBlock.Header);
        BlockFileHoldAssertions.AssertNotHeld(siblingPath);
        if (cacheMode == CacheMode.Disabled)
        {
            Assert.IsFalse(File.Exists(siblingPath));
        }

        Assert.IsFalse(File.Exists(Path.Combine(_directories.CacheDirectory, CacheDirectory.BlockFileName('U', 123))));
        await using var reopened = await FileIndex.OpenAsync(options with
        {
            ScanProgress = null,
            DriveOpenProgress = null
        }, CancellationToken.None);
        AssertDrive(reopened, 'U', ProducerKind.Enumeration);
        Assert.AreEqual(cacheMode == CacheMode.Persistent ? CacheSlotState.OwnedCanonical : null,
            reopened.Drives.Single(drive => drive.Drive == 'U').Block?.CacheSlot);
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

    sealed class CancelEnumerationAfterSiblingSettles(CancellationTokenSource cancellation, Task siblingSettled)
        : IProgress<IndexScanProgress>
    {
        public void Report(IndexScanProgress value)
        {
            if (value.Drive == 'U' && value.Phase != IndexScanPhase.Finished)
            {
                TestGate.WaitSynchronously(siblingSettled);
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        }
    }
}
