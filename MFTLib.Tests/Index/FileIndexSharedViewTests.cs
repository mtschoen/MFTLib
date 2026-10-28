using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
public sealed class FileIndexSharedViewTests
{
    [TestMethod]
    public async Task Anchor_FromAnotherViewOfTheVolume_IsRejected()
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var anchor = source.Find(Path.Combine(fixture.SourcePath, "deeper"))!.Value;
        var query = new SearchQuery(null, Subtree: anchor);

        var failure = Assert.ThrowsExactly<ArgumentException>(() => volume.Search(query));

        Assert.Contains("another view", failure.Message);
        Assert.IsNotEmpty(source.Search(query));
    }

    [TestMethod]
    public async Task Anchor_AfterAShareRescan_IsRejectedOnBothViews()
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));
        var volumeAnchor = volume.Root();
        var sourceAnchor = source.Root();

        await volume.RescanAsync(CancellationToken.None);

        Assert.ThrowsExactly<ArgumentException>(() => volume.Search(new SearchQuery(null, Subtree: volumeAnchor)));
        Assert.ThrowsExactly<ArgumentException>(() => source.Search(new SearchQuery(null, Subtree: sourceAnchor)));
        Assert.AreEqual("src", sourceAnchor.Name, "retained handles still read their retired mapping");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ViewOfflineAndBack_FollowsItsResolvedPath(bool alias)
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        var namedPath = alias ? Path.Combine(fixture.TreeRoot, "alias") : fixture.SourcePath;
        Directory.CreateDirectory(namedPath);
        var resolved = fixture.SourcePath;
        var options = fixture.Options(RootKey.FromDirectory(namedPath));
        if (alias)
        {
            options = options with { FinalPath = _ => new FinalPathResolution(resolved, null) };
        }
        await using var source = await FileIndex.OpenAsync(options, CancellationToken.None);

        await SharedScanFixture.ApplyAsync(volume, SharedScanFixture.Record(30, 5, "away", UsnReason.RenameNewName, FileAttributes.Directory));
        Assert.AreEqual(RootState.Offline, source.Status.State);
        Assert.IsNull(source.Status.Block);
        Assert.IsEmpty(source.Search(new SearchQuery(null)));
        Assert.AreEqual(RootState.Ready, volume.Status.State);
        await SharedScanFixture.ApplyAsync(volume, SharedScanFixture.Record(30, 5, "src", UsnReason.RenameNewName, FileAttributes.Directory));

        Assert.AreEqual(RootState.Ready, source.Status.State);
        Assert.IsNotNull(source.Find(Path.Combine(resolved, "a.txt")));
        Assert.AreEqual(RootKey.FromDirectory(namedPath), source.Status.Root);
    }

    [TestMethod]
    public async Task OfflineAtOpen_NeverAttaches()
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        var absent = Path.Combine(fixture.TreeRoot, "gone");
        await using var missing = await fixture.OpenAsync(RootKey.FromDirectory(absent));

        Assert.AreEqual(RootState.Offline, missing.Status.State);
        Assert.AreEqual(1, fixture.Registry.LiveScanCount);
        Directory.CreateDirectory(absent);
        Assert.AreEqual(RootOperationOutcome.Failed, (await missing.RescanAsync(CancellationToken.None)).Outcome);
        Assert.AreEqual(RootState.Offline, missing.Status.State);
        Assert.AreEqual(1, fixture.ProductionCount);
    }

    [TestMethod]
    public async Task Root_AndParent_ArePerView()
    {
        using var fixture = new SharedScanFixture();
        await using var volume = await fixture.OpenAsync(fixture.Volume);
        await using var source = await fixture.OpenAsync(RootKey.FromDirectory(fixture.SourcePath));

        Assert.IsNull(source.Root().Parent);
        Assert.IsNotNull(volume.Find(fixture.SourcePath)!.Value.Parent);
        Assert.AreEqual(fixture.SourcePath, source.Root().Path);
    }
}
