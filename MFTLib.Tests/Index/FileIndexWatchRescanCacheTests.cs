using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexWatchRescanCacheTests
{
    public TestContext TestContext { get; set; } = null!;

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedProduction_RestoresTheRenamedFileWithLiveJournalUpdates(bool cancel)
    {
        var directory = Directory.CreateTempSubdirectory("mftlib-rescan-restore-");
        var production = new TestGate();
        var rescanRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = TestContext.CancellationTokenSource.Token;
        try
        {
            var source = new ScriptedWatchSource();
            await using var index = await FileIndex.OpenAsync(new FileIndexOptions
            {
                Drives = [new IndexedDrive('T', directory.FullName, 1)],
                CacheDirectory = directory.FullName,
                ProducerPolicy = ProducerPolicy.Mft,
                MftSource = new MftIndexSource(async (request, cancellationToken) =>
                {
                    if (rescanRequested.Task.IsCompleted)
                    {
                        production.MarkEntered();
                        await production.WaitForReleaseAsync(cancellationToken);
                        throw new IOException("production failed after rename");
                    }

                    SeededBlocks.Write(request.BlockPath, 1, WatchHarness.JournalIdentifier, 100, moment: SeededBlocks.SeededMoment);
                    return new MftBlockProduceResult(BlockFile.Open(request.BlockPath, 1, out _)!,
                        WatchHarness.JournalIdentifier, 100, 0);
                }, source)
            }, token);
            try
            {
                await index.StartWatchingAsync('T', token);
                var original = index.Root('T').DriveBlock;
                var handle = source.WatchFor('T');
                var catchUp = index.WaitForCatchUpAsync('T', token);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                rescanRequested.SetResult();
                var rescan = index.RescanAsync('T', cancellation.Token);
                await production.Entered.WaitAsync(ScriptedWatchSource.HangGuard);
                Assert.AreEqual(0, handle.DisposeCount);
                Assert.AreEqual(1, Directory.GetFiles(directory.FullName, "*.retired-*").Length);
                await handle.Publish(WatchHarness.Batch(9, "during.txt", 700));
                if (cancel)
                {
                    await cancellation.CancelAsync();
                    await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => rescan);
                }
                else
                {
                    production.Release();
                    await WatchDeduplicationTestSupport.ThrowsAsync<InvalidOperationException>(() => rescan);
                }

                Assert.AreSame(original, index.Root('T').DriveBlock);
                Assert.AreSame(handle, source.WatchFor('T'));
                Assert.AreEqual(1, source.TargetsFor('T').Count);
                Assert.IsFalse(catchUp.IsCompleted);
                Assert.AreEqual(1, index.Search(new SearchQuery("during.txt", NameMatchMode.Exact), token).Count);
                Assert.AreEqual(0, Directory.GetFiles(directory.FullName, "*.retired-*").Length);
                using var restored = BlockFile.Open(original.Block.Path, 1, out var validation);
                Assert.IsNotNull(restored, validation.ToString());
                Assert.AreEqual(700L, restored.Header.UsnNextUsn);
                await handle.Publish(new DriveCaughtUp());
                await catchUp.WaitAsync(ScriptedWatchSource.HangGuard);
                await index.StopWatchingAsync('T', token);
            }
            finally
            {
                production.Release();
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
