using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public sealed class BrokerFileIndexRescanTests
{
    [TestMethod]
    public async Task Rescan_UsesTheSharedBrokerAndRearmsOnlyItsDrive()
    {
        var batchesC = Channel.CreateUnbounded<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();
        var batchesD = Channel.CreateUnbounded<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)>();
        var firstC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstD = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appliedD = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appliedC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int armsC = 0, armsD = 0, scansC = 0;

        async IAsyncEnumerable<(UsnJournalEntry[] Entries, UsnJournalCursor Cursor)> Watch(
            string drive, UsnJournalCursor cursor, [EnumeratorCancellation] CancellationToken token)
        {
            if (drive == "C")
            {
                if (Interlocked.Increment(ref armsC) == 1)
                {
                    firstC.TrySetResult();
                }
                else
                {
                    secondC.TrySetResult();
                }
            }
            else
            {
                Interlocked.Increment(ref armsD);
                firstD.TrySetResult();
            }
            var channel = drive == "C" ? batchesC : batchesD;
            await foreach (var batch in channel.Reader.ReadAllAsync(token))
            {
                yield return batch;
            }
        }

        IEnumerable<IReadOnlyList<MftRecord>> Scan(string drive,
            IProgress<BlockWriteProgress>? progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var scanNumber = drive == "C" ? Interlocked.Increment(ref scansC) : 1;
            if (drive == "C" && scanNumber == 2)
            {
                batchesD.Writer.TryWrite(([JournalEntryFactory.Create(40, 12600, "during.txt", UsnReason.FileCreate)],
                    new UsnJournalCursor(71, 12601)));
            }
            yield return [new MftRecord(5, 5, new MftRecordFields(3), ".", null),
                new MftRecord(20, 5, new MftRecordFields(1), $"scan-{scanNumber}.txt", null)];
        }

        await using var harness = new InProcessBlockBrokerHarness(recordBatches: Scan,
            catchUp: (_, cursor) => ([], cursor), watchDrive: Watch);
        var token = harness.CancellationToken;
        var producer = new BrokerMftBlockProducer(harness.ConnectAsync);
        var cache = Path.Combine(Path.GetTempPath(), $"broker-rescan-{Guid.NewGuid():N}");
        try
        {
            await using var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Drives = [new IndexedDrive('C', Path.GetTempPath(), 123),
                    new IndexedDrive('D', Path.GetTempPath(), 456)],
                CacheDirectory = cache,
                NoCache = true,
                MftProducer = producer.CreateProducer(),
                WatchSource = producer.CreateWatchSource()
            }, token);
            index.Changed += change =>
            {
                if (change.Entry.Name == "during.txt")
                {
                    appliedD.TrySetResult();
                }
                if (change.Entry.Name == "after.txt")
                {
                    appliedC.TrySetResult();
                }
            };
            await index.StartWatchingAsync(token);
            await Task.WhenAll(firstC.Task, firstD.Task).WaitAsync(token);
            var rescanTask = index.RescanAsync('C', token);
            await appliedD.Task.WaitAsync(token);
            await rescanTask;
            await secondC.Task.WaitAsync(token);
            Assert.AreEqual(2, scansC);
            Assert.AreEqual(2, armsC);
            Assert.AreEqual(1, armsD);
            Assert.AreEqual("scan-2.txt", NamePool.ReadRowName(harness.CreatedBlock!, 20).ToString());
            Assert.AreEqual(1, index.FindByName("scan-2.txt", token).Count);
            Assert.AreEqual(1, index.FindByName("scan-1.txt", token).Count);
            Assert.IsTrue(index.Drives.All(drive => drive.WatchFailureMessage == null));
            batchesC.Writer.TryWrite(([JournalEntryFactory.Create(41, 12700, "after.txt", UsnReason.FileCreate)],
                new UsnJournalCursor(71, 12701)));
            await appliedC.Task.WaitAsync(token);
            await index.StopWatchingAsync(token);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }
}
