using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class FileIndexSharedDisposalTests
{
    [TestMethod]
    public async Task Dispose_CancelsOnlyItsOwnQueries()
    {
        using var fixture = new SharedScanFixture();
        await using var cleanup = new IndexDisposalScope();
        var volume = cleanup.Add(await fixture.OpenAsync(fixture.Volume));
        var source = cleanup.Add(await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath)));
        var firstGate = new TestGate();
        var secondGate = new TestGate();
        volume.Hooks.ReaderAdmitted = () =>
        {
            firstGate.MarkEntered();
            firstGate.WaitForRelease();
        };
        source.Hooks.ReaderAdmitted = () =>
        {
            secondGate.MarkEntered();
            secondGate.WaitForRelease();
        };
        var first = QueryOnWorkerAsync(volume);
        var second = QueryOnWorkerAsync(source);
        try
        {
            await Task.WhenAll(firstGate.Entered, secondGate.Entered).WaitAsync(HangGuard);
            var disposal = volume.DisposeAsync().AsTask();
            Assert.IsTrue(volume.DisposalToken.IsCancellationRequested);
            Assert.IsFalse(source.DisposalToken.IsCancellationRequested);
            firstGate.Release();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => first);
            await disposal.WaitAsync(HangGuard);
            Assert.IsFalse(second.IsCompleted);
        }
        finally
        {
            firstGate.Release();
            secondGate.Release();
        }
        Assert.IsNotEmpty(await second);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DisposeOrder_EitherWay_KeepsTheOtherViewServing(bool volumeViewFirst)
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var cleanup = new IndexDisposalScope();
        var volume = cleanup.Add(await fixture.OpenAsync(fixture.Volume));
        var source = cleanup.Add(await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath)));
        await volume.StartWatchingAsync(CancellationToken.None);
        await source.StartWatchingAsync(CancellationToken.None);
        await source.WaitForCatchUpAsync(CancellationToken.None);
        var first = volumeViewFirst ? volume : source;
        var second = volumeViewFirst ? source : volume;
        var mapped = second.CurrentBlock!.Mapped;

        await first.DisposeAsync();

        Assert.AreEqual(RootState.Ready, second.Status.State);
        Assert.IsNotNull(second.Find(Path.Combine(fixture.SourcePath, "a.txt")));
        Assert.IsNotEmpty(second.Search(new SearchQuery("a.txt")));
        Assert.AreEqual(WatchState.CaughtUp, second.Status.Watch.State);
        Assert.IsFalse(mapped.IsReleased);
        await second.DisposeAsync();
        Assert.IsTrue(mapped.ReleaseState.IsReleaseComplete);
        Assert.AreEqual(0, fixture.Registry.LiveScanCount);
        var reopened = cleanup.Add(await fixture.OpenAsync(fixture.Volume));
        Assert.AreEqual(CacheLocation.OwnedCanonical, reopened.Status.Block!.CacheLocation);
        GC.KeepAlive(mapped);
    }

    [TestMethod]
    public async Task Dispose_NotLast_DoesNotWaitForAnotherViewsBorrow()
    {
        using var fixture = new SharedScanFixture();
        await using var cleanup = new IndexDisposalScope();
        var volume = cleanup.Add(await fixture.OpenAsync(fixture.Volume));
        var source = cleanup.Add(await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath)));
        using var rows = source.Enumerate(new SearchQuery(null)).GetEnumerator();
        Assert.IsTrue(rows.MoveNext());

        await volume.DisposeAsync().AsTask().WaitAsync(HangGuard);

        Assert.IsTrue(rows.MoveNext());
        Assert.AreEqual(RootState.Ready, source.Status.State);
    }

    [TestMethod]
    public async Task Dispose_Last_UnmapsAfterItsBorrowsDrain()
    {
        using var fixture = new SharedScanFixture();
        await using var cleanup = new IndexDisposalScope();
        var volume = cleanup.Add(await fixture.OpenAsync(fixture.Volume));
        var source = cleanup.Add(await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath)));
        var mapped = source.CurrentBlock!.Mapped;
        await volume.DisposeAsync();
        var rows = source.Enumerate(new SearchQuery(null)).GetEnumerator();
        Task disposal;
        try
        {
            Assert.IsTrue(rows.MoveNext());
            disposal = source.DisposeAsync().AsTask();
            Assert.IsFalse(disposal.IsCompleted);
            Assert.IsFalse(mapped.ReleaseState.IsReleaseComplete);
        }
        finally
        {
            rows.Dispose();
        }
        await disposal.WaitAsync(HangGuard);
        Assert.IsTrue(mapped.ReleaseState.IsReleaseComplete);
        GC.KeepAlive(mapped);
    }

    [TestMethod]
    public async Task Dispose_HandlesOfTheDisposedView_ThrowWhileTheOtherViewsRead()
    {
        using var fixture = new SharedScanFixture();
        await using var cleanup = new IndexDisposalScope();
        var volume = cleanup.Add(await fixture.OpenAsync(fixture.Volume));
        var source = cleanup.Add(await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath)));
        var first = volume.Find(Path.Combine(fixture.SourcePath, "a.txt"))!.Value;
        var second = source.Find(Path.Combine(fixture.SourcePath, "a.txt"))!.Value;

        await volume.DisposeAsync();

        Assert.IsTrue(first.IsDisposed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => first.Name);
        Assert.AreEqual("a.txt", second.Name);
    }

    [TestMethod]
    public async Task Dispose_WaitsForADeliveryInsideItsHandler_AndNoHandlerRunsAfter()
    {
        using var fixture = new SharedScanFixture();
        await using var cleanup = new IndexDisposalScope();
        var volume = cleanup.Add(await fixture.OpenAsync(fixture.Volume));
        var source = cleanup.Add(await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath)));
        var gate = new TestGate();
        var delivered = 0;
        volume.Changed += _ =>
        {
            Interlocked.Increment(ref delivered);
            gate.MarkEntered();
            gate.WaitForRelease();
        };
        var batch = SharedScanFixture.ApplyAsync(volume, SharedScanFixture.Record(40, 30, "new.txt", UsnReason.FileCreate));
        await gate.Entered.WaitAsync(HangGuard);
        var disposal = volume.DisposeAsync().AsTask();
        try
        {
            Assert.IsFalse(disposal.IsCompleted);
        }
        finally
        {
            gate.Release();
            await batch;
            await disposal;
        }
        await SharedScanFixture.ApplyAsync(source, SharedScanFixture.Record(41, 30, "later.txt", UsnReason.FileCreate));
        Assert.AreEqual(1, delivered);
    }

    [TestMethod]
    public async Task Reopen_RacingTheLastDetach_OwnsTheCanonicalSlot()
    {
        using var fixture = new SharedScanFixture();
        await using var cleanup = new IndexDisposalScope();
        var index = cleanup.Add(await fixture.OpenAsync(fixture.Volume));
        var gate = new TestGate();
        index.CurrentBlock!.ReleaseState.Hooks._releaseStarted = () =>
        {
            gate.MarkEntered();
            gate.WaitForRelease();
        };
        var disposal = DisposeOnWorkerAsync(index);
        await gate.Entered.WaitAsync(HangGuard);
        var reopening = fixture.OpenAsync(fixture.Volume);
        try
        {
            Assert.IsFalse(reopening.IsCompleted);
        }
        finally
        {
            gate.Release();
        }
        await disposal;
        await using var reopened = await reopening;
        Assert.AreEqual(CacheLocation.OwnedCanonical, reopened.Status.Block!.CacheLocation);
        Assert.AreEqual(1, fixture.ProductionCount);
    }

    [TestMethod]
    public async Task Dispose_TwiceAndConcurrently_IsIdempotent()
    {
        using var fixture = new SharedScanFixture();
        await using var cleanup = new IndexDisposalScope();
        var index = cleanup.Add(await fixture.OpenAsync(fixture.Volume));
        var rows = index.Enumerate(new SearchQuery(null)).GetEnumerator();
        Task first;
        Task second;
        try
        {
            Assert.IsTrue(rows.MoveNext());
            first = index.DisposeAsync().AsTask();
            second = index.DisposeAsync().AsTask();
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted);
        }
        finally
        {
            rows.Dispose();
        }
        await Task.WhenAll(first, second).WaitAsync(HangGuard);
        Assert.AreEqual(0, fixture.Registry.LiveScanCount);
    }

    static Task DisposeOnWorkerAsync(FileIndex index) => Task.Run(async () => await index.DisposeAsync());

    static Task<IReadOnlyList<FileEntry>> QueryOnWorkerAsync(FileIndex index) => Task.Run(() => index.Search(new SearchQuery(null)));
}
