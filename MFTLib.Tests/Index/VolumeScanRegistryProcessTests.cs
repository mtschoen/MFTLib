using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests.Index;

[TestClass]
[DoNotParallelize]
public sealed class VolumeScanRegistryProcessTests
{
    [TestMethod]
    public async Task RelativeCacheDirectory_StaysBoundToTheDirectoryResolvedAtOpen()
    {
        using var fixture = new SharedScanFixture();
        using var firstDirectory = new TempDirectory("first-working-directory");
        using var secondDirectory = new TempDirectory("second-working-directory");
        Directory.CreateDirectory(Path.Combine(secondDirectory.Path, "cache"));
        var previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = firstDirectory.Path;
            await using var index = await FileIndex.OpenAsync(fixture.Options(fixture.Volume) with
            {
                CacheDirectory = "cache"
            }, CancellationToken.None);
            Environment.CurrentDirectory = secondDirectory.Path;

            Assert.AreEqual(RootOperationOutcome.Succeeded, (await index.RescanAsync(CancellationToken.None)).Outcome);

            Assert.HasCount(1, Directory.GetFiles(Path.Combine(firstDirectory.Path, "cache"), "*.mlix"));
            Assert.IsEmpty(Directory.GetFiles(Path.Combine(secondDirectory.Path, "cache"), "*.mlix"));
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    readonly List<VolumeScanKey> _keys = [];

    [TestCleanup]
    public void NoScanOfThisClassStaysLive()
    {
        foreach (var key in _keys)
        {
            Assert.IsFalse(VolumeScanRegistry.Process.IsLive(key));
        }
    }

    [TestMethod]
    public async Task DefaultOptions_ShareThroughTheProcessRegistry()
    {
        using var fixture = new SharedScanFixture();
        var options = fixture.Options(fixture.Volume) with { ScanRegistry = null };
        var resolved = RootResolution.Resolve(RootKind.PrepareOpenOptions(options));
        var key = VolumeScanKey.For(resolved, options, fixture.CacheDirectoryPath);
        Assert.IsNotNull(key);
        _keys.Add(key.Value);
        await using var volume = await FileIndex.OpenAsync(options, CancellationToken.None);
        await using var source = await FileIndex.OpenAsync(options with
        {
            Root = RootKey.FromDirectory(fixture.SourcePath)
        }, CancellationToken.None);

        Assert.AreEqual(1, fixture.ProductionCount);
        Assert.AreSame(volume.CurrentBlock!.Mapped, source.CurrentBlock!.Mapped);
        Assert.IsTrue(VolumeScanRegistry.Process.IsLive(key.Value));
    }
}
