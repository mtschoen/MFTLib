using MFTLib.Index;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     The swap gate admits one waiter at a time, and <see cref="FileIndex.DisposeAsync" /> sets
///     the disposal flag before it queues on that gate. A caller already queued behind an in-flight
///     scan therefore reaches the front after the index has been declared disposed, past the check
///     at the top of its own method. It has to notice.
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
    public async Task RescanAsync_AdmittedToTheGateAfterDisposeAsync_ThrowsInsteadOfPublishing()
    {
        using var progress = new BlockOnFirstReport();
        var index = await FileIndex.OpenAsync(Options(progress), CancellationToken.None);
        try
        {
            progress.Armed = true;

            // Parks inside the scan while holding the gate, so both calls below queue behind it.
            var gateHolder = index.RescanAsync('T', CancellationToken.None);
            Assert.IsTrue(progress.WaitUntilParked(),
                "the first rescan never reached its progress callback, so it never took the gate");

            // Runs its own disposal check and queues on the gate while the flag is still clear.
            var queuedRescan = index.RescanAsync('T', CancellationToken.None);

            // Sets the disposal flag synchronously, before awaiting the gate behind the rescan
            // above. That ordering is exactly what the re-check inside the gate exists for.
            var disposal = index.DisposeAsync();

            progress.Release();
            await gateHolder;

            var thrown = await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => queuedRescan);
            StringAssert.Contains(thrown.ObjectName, nameof(FileIndex),
                "the index itself must report the disposal, not the swap gate: a gate disposed out "
                + "from under a waiter reports the semaphore and hides what actually went wrong");
            await disposal;
        }
        finally
        {
            progress.Release();
            await index.DisposeAsync();
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
