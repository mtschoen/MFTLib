using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class FileIndexSharedRoutingTests
{
    [TestMethod]
    public async Task DirectApply_TwoThrowingViews_DeliversBothAndAggregatesTheirFailures()
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var first = new IOException("volume subscriber failed");
        var second = new IOException("directory subscriber failed");
        volume.Changed += _ => throw first;
        source.Changed += _ => throw second;

        var failure = await Assert.ThrowsExactlyAsync<AggregateException>(() => SharedScanFixture.ApplyAsync(volume,
            SharedScanFixture.Record(40, 30, "new.txt", UsnReason.FileCreate)));

        Assert.AreSequenceEqual(new Exception[] { first, second }, failure.InnerExceptions);
        Assert.IsNotNull(source.Find(Path.Combine(fixture.SourcePath, "new.txt")));
    }

    [TestMethod]
    [DataRow(40, 5, 0, 0)]
    [DataRow(40, 34, 1, 1)]
    public async Task Route_OutsideAndNestedViews_UsesContainment(int row, int parent, int sourceCount, int nestedCount)
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        await using var nested = await fixture.OpenAsync(RootKey.FromDirectory(Path.Combine(fixture.SourcePath, "deeper")));
        var sourceChanges = new List<FileChange>();
        var nestedChanges = new List<FileChange>();
        var volumeChanges = new List<FileChange>();
        source.Changed += sourceChanges.Add;
        nested.Changed += nestedChanges.Add;
        volume.Changed += volumeChanges.Add;

        await SharedScanFixture.ApplyAsync(volume, SharedScanFixture.Record((ulong)row, (ulong)parent, "new.txt", UsnReason.FileCreate));

        Assert.HasCount(1, volumeChanges);
        Assert.HasCount(sourceCount, sourceChanges);
        Assert.HasCount(nestedCount, nestedChanges);
    }

    [TestMethod]
    public async Task Route_TheViewRootRenamedAway_ReachesTheViewThenItGoesOffline()
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var changes = new List<FileChange>();
        source.Changed += changes.Add;

        await SharedScanFixture.ApplyAsync(volume, SharedScanFixture.Record(30, 5, "renamed", UsnReason.RenameNewName, FileAttributes.Directory));

        Assert.HasCount(1, changes);
        Assert.AreEqual(FileChangeKind.Renamed, changes[0].Kind);
        Assert.AreEqual(RootState.Offline, source.Status.State);
        Assert.AreEqual(RootState.Ready, volume.Status.State);
    }

    [TestMethod]
    public async Task Route_EntriesAreMintedOnTheReceivingView()
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var changes = await SharedScanFixture.ApplyAsync(source,
            SharedScanFixture.Record(34, 30, "deeper", UsnReason.BasicInfoChange, FileAttributes.Directory));
        var anchor = changes.Single().Entry;

        Assert.AreEqual(source.Status.Root, anchor.Root);
        Assert.IsNotEmpty(source.Search(new SearchQuery(null, Subtree: anchor)));
        Assert.ThrowsExactly<ArgumentException>(() => volume.Search(new SearchQuery(null, Subtree: anchor)));
    }

    [TestMethod]
    public async Task Route_ABatchConcurrentWithAttach_IsSeenExactlyOnce()
    {
        var watches = new ScriptedWatchSource { CatchUpOnStart = true };
        using var fixture = new SharedScanFixture(watches);
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await volume.StartWatchingAsync(CancellationToken.None);
        var gate = new TestGate();
        volume.Hooks.BeforeWatchChanged = _ =>
        {
            gate.MarkEntered();
            gate.WaitForRelease();
        };
        var watch = watches.Watches.Single();
        var batch = watch.Publish(new JournalBatch([SharedScanFixture.Record(40, 32, "first.txt", UsnReason.FileCreate)], 7, 5001));
        await gate.Entered.WaitAsync(HangGuard);
        FileIndex work;
        var delivered = 0;
        try
        {
            work = await fixture.OpenAsync(RootKey.FromDirectory(fixture.WorkPath));
            work.Changed += _ => Interlocked.Increment(ref delivered);
        }
        finally
        {
            gate.Release();
        }
        await using var owned = work;
        await batch;
        Assert.IsNotNull(work.Find(Path.Combine(fixture.WorkPath, "first.txt")));
        Assert.AreEqual(0, delivered);
        await watch.Publish(new JournalBatch([SharedScanFixture.Record(41, 32, "second.txt", UsnReason.FileCreate)], 7, 5002));
        Assert.AreEqual(1, delivered);
    }

    [TestMethod]
    public async Task Route_AChangeInsideAView_ReachesItAndTheVolumeView()
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        await using var work = await fixture.OpenAsync(RootKey.FromDirectory(fixture.WorkPath));
        var volumeChanges = new List<FileChange>();
        var sourceChanges = new List<FileChange>();
        var workChanges = new List<FileChange>();
        volume.Changed += volumeChanges.Add;
        source.Changed += sourceChanges.Add;
        work.Changed += workChanges.Add;

        var returned = await SharedScanFixture.ApplyAsync(source, SharedScanFixture.Record(31, 30, "a.txt", UsnReason.DataOverwrite));

        Assert.HasCount(1, volumeChanges);
        Assert.HasCount(1, sourceChanges);
        Assert.IsEmpty(workChanges);
        Assert.AreSequenceEqual(sourceChanges, returned);
        Assert.AreSame(source.CurrentBlock, sourceChanges[0].Entry.NamedRootBlock);
        Assert.AreNotEqual(sourceChanges[0].Entry, volumeChanges[0].Entry);
    }

    [TestMethod]
    public async Task Route_AMoveAcrossViews_ReachesBothAsRenamed()
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        await using var work = await fixture.OpenAsync(RootKey.FromDirectory(fixture.WorkPath));
        var sourceChanges = new List<FileChange>();
        var workChanges = new List<FileChange>();
        source.Changed += sourceChanges.Add;
        work.Changed += workChanges.Add;

        await SharedScanFixture.ApplyAsync(volume, SharedScanFixture.Record(31, 32, "a.txt", UsnReason.RenameNewName));

        Assert.HasCount(1, sourceChanges);
        Assert.HasCount(1, workChanges);
        Assert.AreEqual(FileChangeKind.Renamed, sourceChanges[0].Kind);
        Assert.AreEqual(FileChangeKind.Renamed, workChanges[0].Kind);
        Assert.AreEqual(Path.Combine(fixture.SourcePath, "a.txt"), workChanges[0].PreviousPath);
        Assert.AreEqual(Path.Combine(fixture.WorkPath, "a.txt"), sourceChanges[0].Path);
        Assert.AreSame(work.CurrentBlock, workChanges[0].Entry.NamedRootBlock);
    }
}
