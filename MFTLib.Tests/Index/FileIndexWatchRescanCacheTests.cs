using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public class FileIndexWatchRescanCacheTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedProduction_RestoresTheRenamedFileWithLiveJournalUpdates(bool cancel)
    {
        using var directory = new TempDirectory("mftlib-rescan-restore");
        var production = new TestGate();
        var rescanRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = TestContext.CancellationToken;
        var source = new ScriptedWatchSource();
        await using var index = await FileIndex.OpenAsync(new FileIndexOptions
        {

            CacheDirectory = directory.Path,
            MftSource = new MftIndexSource(async (request, cancellationToken) =>
            {
                if (rescanRequested.Task.IsCompleted)
                {
                    production.MarkEntered();
                    await production.WaitForReleaseAsync(cancellationToken);
                    throw new IOException("production failed after rename");
                }

                SeededBlocks.Write(request.BlockPath, 1, WatchHarness.JournalIdentifier, 100, moment: SeededBlocks.SeededMoment);
                return new MftBlockProduceResult(BlockFile.Open(request.BlockPath, 1, out _)!, 0);
            }, source.IndexSource)
        }.WithSyntheticRoot(SyntheticRoot.Create(new VolumeKey('T'), directory.Path, 1)), token);
        try
        {
            await index.StartWatchingAsync(token);
            var original = index.Root(TestContext.CancellationToken).RootBlock;
            var handle = source.WatchFor('T');
            var catchUp = index.WaitForCatchUpAsync(token);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            rescanRequested.SetResult();
            var rescan = index.RescanAsync(cancellation.Token);
            await production.Entered.WaitAsync(HangGuard, TestContext.CancellationToken);
            Assert.AreEqual(0, handle.DisposeCount);
            Assert.HasCount(1, Directory.GetFiles(directory.Path, "*.retired-*"));
            await handle.Publish(WatchHarness.Batch(9, "during.txt", 700));
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => rescan);
            }
            else
            {
                production.Release();
                await RootResultAssertions.FailureAsync<InvalidOperationException>(() => rescan);
            }

            Assert.AreSame(original, index.Root(TestContext.CancellationToken).RootBlock);
            Assert.AreSame(handle, source.WatchFor('T'));
            Assert.HasCount(1, source.TargetsFor('T'));
            Assert.IsFalse(catchUp.IsCompleted);
            Assert.HasCount(1, index.Search(new SearchQuery("during.txt", NameMatchMode.Exact), token));
            Assert.IsEmpty(Directory.GetFiles(directory.Path, "*.retired-*"));
            using var restored = BlockFile.Open(original.Block.Path, 1, out var validation);
            Assert.IsNotNull(restored, validation.ToString());
            Assert.AreEqual(700L, restored.Header.UsnNextUsn);
            await handle.Publish(new VolumeCaughtUp());
            await catchUp.WaitAsync(HangGuard, TestContext.CancellationToken);
            await index.StopWatchingAsync(token);
        }
        finally
        {
            production.Release();
        }
    }
}
