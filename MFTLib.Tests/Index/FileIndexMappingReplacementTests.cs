using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

/// <summary>
///     MFTLib issue 953: an open index whose lineage replaces its mapping. Queries in flight finish on the mapping they
///     pinned, handles stay equal, disposal during and after the replacement waits for every reader before it unmaps
///     anything, and the retired mapping's renamed-aside file is deleted when the lineage is released.
/// </summary>
[TestClass]
public sealed class FileIndexMappingReplacementTests : IndexTreeTestBase
{
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    FileIndexOptions Options() => Fixture.Options(volumeSerial: UniqueVolumeSerial);

    string CanonicalPath =>
        Path.Combine(CacheDirectoryPath, CacheDirectory.BlockFileName(new VolumeKey('T'), UniqueVolumeSerial));

    [TestMethod]
    public async Task Replace_KeepsHandlesEqualAndLive_AndNewQueriesReadTheReplacement()
    {
        await using var index = await FileIndex.OpenAsync(Options(), TestContext.CancellationToken);
        var held = FindReadme(index);
        var statusBefore = index.Status.Block;

        Grow(index, renameReadmeTo: "renamed.md");

        Assert.AreEqual(FileEntry.Create(index.CurrentBlock!, held.RowIndex), held);
        Assert.IsFalse(held.IsDisposed);
        Assert.AreEqual("renamed.md", held.Name);
        Assert.AreEqual(held, index.Search(new SearchQuery("renamed.md", NameMatchMode.Exact),
            TestContext.CancellationToken).Single());
        Assert.IsEmpty(index.Search(new SearchQuery("readme.md", NameMatchMode.Exact), TestContext.CancellationToken));
        Assert.AreEqual(statusBefore!.LiveRowCount, index.Status.Block!.LiveRowCount);
    }

    [TestMethod]
    public async Task RowEnumerationInFlight_KeepsTheRetiredMappingToTheEnd()
    {
        await using var index = await FileIndex.OpenAsync(Options(), TestContext.CancellationToken);
        Growth? growth = null;
        var names = new List<string>();
        foreach (var row in index.EnumerateRows(new SearchQuery(null), TestContext.CancellationToken))
        {
            growth ??= Grow(index, renameReadmeTo: "renamed.md");
            names.Add(row.Name.ToString());
        }

        CollectionAssert.Contains(names, "readme.md");
        CollectionAssert.DoesNotContain(names, "renamed.md");
        Assert.IsNotNull(growth);
        Assert.AreEqual(0, growth.Retired.ReleaseState.Hooks.OutstandingBorrowCount);
        var after = new List<string>();
        foreach (var row in index.EnumerateRows(new SearchQuery(null), TestContext.CancellationToken))
        {
            after.Add(row.Name.ToString());
        }

        CollectionAssert.Contains(after, "renamed.md");
        CollectionAssert.DoesNotContain(after, "readme.md");
    }

    [TestMethod]
    public async Task DisposeAfterReplace_ReleasesBothMappings_AndDeletesTheRetiredFile()
    {
        var index = await FileIndex.OpenAsync(Options(), TestContext.CancellationToken);
        var held = FindReadme(index);
        var growth = Grow(index, renameReadmeTo: null);
        Assert.IsTrue(File.Exists(growth.RetiredPath), "the retired file was deleted while its lineage lives");

        await index.DisposeAsync().AsTask().WaitAsync(HangGuard, TestContext.CancellationToken);

        Assert.IsTrue(growth.Retired.ReleaseState.IsReleaseComplete);
        Assert.IsTrue(held.IsDisposed);
        Assert.IsFalse(File.Exists(growth.RetiredPath));
        BlockFileHoldAssertions.AssertNotHeld(CanonicalPath);
    }

    [TestMethod]
    public async Task DisposeDuringReplace_WaitsForTheReaderOfTheRetiredMapping_BeforeUnmappingIt()
    {
        var index = await FileIndex.OpenAsync(Options(), TestContext.CancellationToken);
        var view = index.CurrentBlock!;
        var reader = index.Enumerate(new SearchQuery(null), TestContext.CancellationToken).GetEnumerator();
        try
        {
            Assert.IsTrue(reader.MoveNext());
            var growth = Grow(index, renameReadmeTo: null);
            Assert.AreEqual(1, growth.Retired.ReleaseState.Hooks.OutstandingBorrowCount);

            var disposal = index.DisposeAsync().AsTask();

            // The view closes before disposal drains its borrows, so from here disposal waits on this reader.
            Assert.IsTrue(SpinWait.SpinUntil(() => view.IsReleased, HangGuard), "disposal never closed the view");
            Assert.IsFalse(disposal.IsCompleted, "disposal finished while a reader held the retired mapping");
            Assert.IsFalse(growth.Retired.ReleaseState.IsReleaseComplete, "the retired mapping was unmapped under its reader");
            Assert.IsTrue(File.Exists(growth.RetiredPath));

            reader.Dispose();
            await disposal.WaitAsync(HangGuard, TestContext.CancellationToken);

            Assert.IsTrue(growth.Retired.ReleaseState.IsReleaseComplete);
            Assert.IsFalse(File.Exists(growth.RetiredPath));
            BlockFileHoldAssertions.AssertNotHeld(CanonicalPath);
        }
        finally
        {
            reader.Dispose();
        }
    }

    [TestMethod]
    public async Task Rescan_StartsANewLineage_AndSealsTheOldOne()
    {
        await using var index = await FileIndex.OpenAsync(Options(), TestContext.CancellationToken);
        var before = index.CurrentBlock!.Mapped;
        await File.WriteAllTextAsync(Path.Combine(TreeRoot, "Documents", "second.md"), "second",
            TestContext.CancellationToken);

        await index.RescanAsync(TestContext.CancellationToken);

        Assert.AreNotSame(before, index.CurrentBlock!.Mapped);
        using var unused = TestBlocks.Create();
        Assert.ThrowsExactly<ObjectDisposedException>(() => before.Replace(unused));
    }

    static FileEntry FindReadme(FileIndex index) =>
        index.Search(new SearchQuery("readme.md", NameMatchMode.Exact)).Single();

    /// <summary>
    ///     Stands in for a watch growth: copies the canonical block to a sibling, renames the canonical file aside and
    ///     schedules its deletion on the retired mapping, renames the copy into place and publishes it. The copy can
    ///     rename the readme row so a read shows which mapping it went through.
    /// </summary>
    Growth Grow(FileIndex index, string? renameReadmeTo)
    {
        var growingPath = CanonicalPath + ".growing-test";
        var retiredPath = CanonicalPath + ".retired-test";
        File.Copy(CanonicalPath, growingPath);
        // aislop-ignore-next-line dotnet/IDISP001 -- ownership moves to the lineage, whose release disposes it
        var replacement = BlockFile.Open(growingPath, UniqueVolumeSerial, out var validation)
                          ?? throw new AssertFailedException($"The copied block did not open: {validation}");
        if (renameReadmeTo is not null)
        {
            var readme = FindReadme(index);
            Assert.AreEqual(WriteRefusal.None, new BlockWriter(replacement).TryRenameRow(readme.RowIndex, renameReadmeTo,
                readme.Parent!.Value.RowIndex));
        }

        File.Move(CanonicalPath, retiredPath);
        File.Move(growingPath, CanonicalPath);
        var retired = index.CurrentBlock!.Mapped.Replace(replacement);
        retired.ReleaseState.ScheduleDeleteAt(retiredPath);
        return new Growth(retired, retiredPath);
    }

    /// <summary>A replacement the test published: the retired mapping and the path its file was renamed aside to.</summary>
    sealed record Growth(BlockMapping Retired, string RetiredPath);

    public TestContext TestContext { get; set; } = null!;
}
