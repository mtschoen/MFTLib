using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     <see cref="FileIndex.DisposeAsync" /> sets the disposal flag and cancels the disposal token
///     before it waits for any drive's gate. A rescan in flight and a rescan queued behind it on the
///     drive's lifecycle gate are both linked to that token, so both end cancelled rather than
///     publishing, and a call made after disposal is refused by the flag.
/// </summary>
[TestClass]
public class FileIndexDisposalRaceTests
{
    static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(30);

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

    FileIndexOptions Options(IProgress<IndexScanProgress> progress)
    {
        return new FileIndexOptions
        {
            Drives = [new IndexedDrive('T', _treeRoot, 0x0BADF00D)],
            CacheDirectory = _cacheDirectory,
            ProducerPolicy = ProducerPolicy.Enumeration,
            Progress = progress
        };
    }

    /// <summary>
    ///     Parks the first armed scan inside its progress callback and holds it there until the
    ///     test lets go, so the scan keeps the swap gate for as long as the test needs. Only the
    ///     first report parks: later ones, and the ones from the opening scan before the blocker is
    ///     armed, pass straight through.
    /// </summary>
    sealed class BlockOnFirstReport : IProgress<IndexScanProgress>, IDisposable
    {
        readonly ManualResetEventSlim _parked = new(initialState: false);
        readonly ManualResetEventSlim _released = new(initialState: false);
        int _alreadyParked;

        public bool Armed { get; set; }

        public void Report(IndexScanProgress value)
        {
            if (!Armed || Interlocked.Exchange(ref _alreadyParked, 1) != 0)
            {
                return;
            }

            _parked.Set();
            _released.Wait();
        }

        public bool WaitUntilParked()
        {
            return _parked.Wait(HandoffTimeout);
        }

        public void Release()
        {
            _released.Set();
        }

        public void Dispose()
        {
            _released.Set();
            _parked.Dispose();
            _released.Dispose();
        }
    }

    [TestMethod]
    public async Task RescanAsync_QueuedOnTheGateWhenDisposeAsyncBegins_IsCancelledInsteadOfPublishing()
    {
        using var progress = new BlockOnFirstReport();
        var index = await FileIndex.OpenAsync(Options(progress), CancellationToken.None);
        try
        {
            progress.Armed = true;
            var original = index.Root('T').DriveBlock;

            // Parks inside the scan while holding the drive's lifecycle gate, so the call below
            // queues behind it.
            var gateHolder = index.RescanAsync('T', CancellationToken.None);
            Assert.IsTrue(progress.WaitUntilParked(),
                "the first rescan never reached its progress callback, so it never took the gate");

            // Runs its own disposal check and queues on the gate while the flag is still clear.
            var queuedRescan = index.RescanAsync('T', CancellationToken.None);

            // Sets the disposal flag and cancels the token both rescans are linked to, then waits
            // for the drive's gates.
            var disposal = index.DisposeAsync();

            progress.Release();
            await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => gateHolder);
            await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => queuedRescan);
            await disposal;
            Assert.IsTrue(original.IsReleased, "the block neither rescan replaced was released by the disposal");
        }
        finally
        {
            progress.Release();
            await index.DisposeAsync();
        }
    }

    /// <summary>
    ///     The same two rescans, reaching their next checkpoints in the window where disposal has
    ///     set its flag but not yet cancelled the disposal token: the scan in flight reaches its
    ///     publish, and the queued rescan takes the gate the first one released. Both were admitted
    ///     before disposal began, so both are cancelled by it, never refused as if they had been
    ///     called after it.
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_AdmittedBeforeDisposal_ReachingACheckpointBeforeTheTokenIsCancelled_IsCancelled()
    {
        using var progress = new BlockOnFirstReport();
        var index = await FileIndex.OpenAsync(Options(progress), CancellationToken.None).WaitAsync(HandoffTimeout);
        try
        {
            progress.Armed = true;
            var original = index.Root('T').DriveBlock;
            var gateHolder = index.RescanAsync('T', CancellationToken.None);
            Assert.IsTrue(progress.WaitUntilParked(),
                "the first rescan never reached its progress callback, so it never took the gate");
            var queuedRescan = index.RescanAsync('T', CancellationToken.None);
            var settledInTheWindow = false;
            Action releaseTheScan = progress.Release;
            index.DisposedFlagSetForTest = () =>
            {
                releaseTheScan();
                try
                {
                    settledInTheWindow = Task.WaitAll([gateHolder, queuedRescan], HandoffTimeout);
                }
                catch (AggregateException)
                {
                    // Both are expected to fault; their exceptions are asserted below.
                    settledInTheWindow = true;
                }
            };

            var disposal = index.DisposeAsync().AsTask();

            Assert.IsTrue(settledInTheWindow, "both rescans settled before disposal cancelled its token");
            await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => gateHolder)
                .WaitAsync(HandoffTimeout);
            await WatchDeduplicationTestSupport.ThrowsAsync<OperationCanceledException>(() => queuedRescan)
                .WaitAsync(HandoffTimeout);
            await disposal.WaitAsync(HandoffTimeout);
            Assert.IsTrue(original.IsReleased, "the block neither rescan replaced was released by the disposal");
        }
        finally
        {
            progress.Release();
            await index.DisposeAsync().AsTask().WaitAsync(HandoffTimeout);
        }
    }

    [TestMethod]
    public async Task RescanAsync_StartedAfterDisposeAsync_ThrowsInsteadOfPublishing()
    {
        using var progress = new BlockOnFirstReport();
        var index = await FileIndex.OpenAsync(Options(progress), CancellationToken.None);
        await index.DisposeAsync();

        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(
            () => index.RescanAsync('T', CancellationToken.None));
    }

    [TestMethod]
    public async Task ApplyJournalEntries_AfterDisposeAsync_ThrowsInsteadOfMutating()
    {
        using var progress = new BlockOnFirstReport();
        var index = await FileIndex.OpenAsync(Options(progress), CancellationToken.None);
        await index.DisposeAsync();

        Assert.ThrowsException<ObjectDisposedException>(
            () => index.ApplyJournalEntries('T', [], journalId: 1, nextUsn: 1));
    }
}
