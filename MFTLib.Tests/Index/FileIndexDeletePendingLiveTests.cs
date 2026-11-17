using System.Collections.Concurrent;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     Live <see cref="FileIndex" /> acceptance test for POSIX delete-pending (<c>$Extend\$Deleted</c>)
///     handling over a real NTFS volume and broker watch. <c>Deleted</c> is required whenever the journal
///     window that ends after the test's own handle closed holds the file's <c>FileDelete</c> record; see
///     <see cref="LiveJournalWindow" />.
/// </summary>
[TestClass]
public sealed class FileIndexDeletePendingLiveTests
{
    const int EventWaitSeconds = 60;

    [TestMethod]
    [TestCategory("RequiresAdmin")]
    public async Task LiveFileIndex_HeldHandleDelete_EmitsDeletePendingHidesFromQueriesThenEmitsDeleted()
    {
        if (!ElevationUtilities.DefaultProvider.IsElevated())
        {
            Assert.Inconclusive("Requires admin");
            return;
        }

        using var tempDirectory = new TempDirectory("mftlib-live-delete-pending");
        var fullDirectoryPath = Path.GetFullPath(tempDirectory.Path);
        var volumeRoot = Path.GetPathRoot(fullDirectoryPath);
        var volumeKey = !string.IsNullOrEmpty(volumeRoot) && volumeRoot.Length >= 2 && volumeRoot[1] == ':'
            ? new VolumeKey(volumeRoot[0])
            : new VolumeKey('C');

        await using var broker = new InProcessBroker(JournalBrokerHost.CreateDefault());
        var brokerProcess = broker.Process;
        var source = new BrokerMftBlockProducer(_ => Task.FromResult(brokerProcess)).CreateIndexSource();
        var options = new FileIndexOptions
        {
            Root = volumeKey,
            Producer = ProducerSelection.MftOnly,
            CacheMode = CacheMode.Disabled,
            MftSource = source
        };

        await using var index = await FileIndex.OpenAsync(options, TestContext.CancellationToken);
        Assert.AreEqual(RootState.Ready, index.Status.State);

        var fileName = $"held-delete-{Guid.NewGuid():N}.txt";
        var filePath = Path.Combine(fullDirectoryPath, fileName);
        var observedChanges = new ConcurrentQueue<FileChange>();
        var deletePendingObserved = new TaskCompletionSource<FileChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletedObserved = new TaskCompletionSource<FileChange>(TaskCreationOptions.RunContinuationsAsynchronously);

        index.Changed += change =>
        {
            if (string.Equals(change.Path, filePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(change.PreviousPath, filePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(change.Entry.Name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                observedChanges.Enqueue(change);
            }

            if (change.Kind == FileChangeKind.DeletePending &&
                string.Equals(change.Path, filePath, StringComparison.OrdinalIgnoreCase))
            {
                deletePendingObserved.TrySetResult(change);
            }
            else if (change.Kind == FileChangeKind.Deleted &&
                     string.Equals(change.Path, filePath, StringComparison.OrdinalIgnoreCase))
            {
                deletedObserved.TrySetResult(change);
            }
        };

        var startResult = await index.StartWatchingAsync(TestContext.CancellationToken);
        Assert.AreEqual(RootOperationOutcome.Succeeded, startResult.Outcome);
        var catchUpResult = await index.WaitForCatchUpAsync(TestContext.CancellationToken);
        Assert.AreEqual(RootOperationOutcome.Succeeded, catchUpResult.Outcome);

        using var volume = MftVolume.Open(volumeKey);
        var cycleStart = volume.Journal.QueryUsnJournalCursor();
        await File.WriteAllTextAsync(filePath, "held-handle-delete", TestContext.CancellationToken);

        FileChange pendingChange;
        using (new FileStream(filePath, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            File.Delete(filePath);

            using var pendingTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            pendingTimeout.CancelAfter(TimeSpan.FromSeconds(EventWaitSeconds));
            pendingChange = await deletePendingObserved.Task.WaitAsync(pendingTimeout.Token);

            Assert.AreEqual(filePath, pendingChange.Path);
            Assert.AreEqual(fileName, pendingChange.Entry.Name);
            Assert.IsNull(pendingChange.PreviousPath);

            Assert.IsEmpty(index.Search(new SearchQuery(fileName) { MatchMode = NameMatchMode.Exact },
                TestContext.CancellationToken));
            Assert.IsEmpty(index.Search(new SearchQuery(fileName)
            {
                MatchMode = NameMatchMode.Exact,
                IncludeDeleted = true
            }, TestContext.CancellationToken));
            Assert.IsNull(index.Find(filePath, TestContext.CancellationToken));
        }

        // NTFS writes the FileDelete record when the last handle closes. When it is in the journal window that
        // ends after this test's handle closed, Deleted is owed and its wait depends only on the product; when it
        // is not, another process (a scanner) still holds the file and owns the timing, so Deleted is not awaited.
        // JournalMutatorTests.DeletePending_HeldHandleDeleteOnSeededFile_ReportsDeletePendingThenDeleted and the
        // DeletePending path-retention tests pin Deleted, its path and its name on injected records.
        var cycleEnd = volume.Journal.QueryUsnJournalCursor();
        var windowRecords = (await LiveJournalWindow.ReadAsync(volume, cycleStart, cycleEnd,
            TestContext.CancellationToken)).SelectMany(batch => batch).ToArray();
        var created = windowRecords
            .Where(e => e.IsCreate && e.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .Select(e => ((ulong RecordNumber, ushort SequenceNumber)?)(e.RecordNumber, e.SequenceNumber))
            .FirstOrDefault();
        Assert.IsNotNull(created, $"The journal window holds no FileCreate record for {fileName}.");
        var lastHandleClosed = windowRecords.Any(e => e.IsDelete && e.RecordNumber == created.Value.RecordNumber
            && e.SequenceNumber == created.Value.SequenceNumber);
        if (lastHandleClosed)
        {
            using var deletedTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            deletedTimeout.CancelAfter(TimeSpan.FromSeconds(EventWaitSeconds));
            var deletedChange = await deletedObserved.Task.WaitAsync(deletedTimeout.Token);

            Assert.AreEqual(pendingChange.Path, deletedChange.Path);
            Assert.AreEqual(filePath, deletedChange.Path);
            Assert.AreEqual(fileName, deletedChange.Entry.Name);
            Assert.IsNull(deletedChange.PreviousPath);
        }
        else
        {
            TestContext.WriteLine($"Another process still held {fileName} after this test's handle closed, so the "
                + "journal holds no FileDelete record yet and Deleted is not awaited.");
        }

        Assert.DoesNotContain(change => change.Kind == FileChangeKind.Renamed, observedChanges,
            "A POSIX delete-pending move into $Extend\\$Deleted must never raise Renamed.");
    }

    public TestContext TestContext { get; set; } = null!;
}
