using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class SyntheticIndexSourceTests
{
    static MftBlockProduceRequest Request(string blockPath) => new()
    {
        Volume = 'T',
        VolumeSerial = 0x1234,
        BlockPath = blockPath,
        DeleteOnClose = true
    };

    [TestMethod]
    public async Task Create_ScansThroughTheSyntheticProducerAndWatchesThroughTheScriptedSource()
    {
        var producer = new SyntheticMftProducer(_ =>
        [
            new SyntheticRow(5, ".", 5) { IsDirectory = true },
            new SyntheticRow(6, "docs", 5) { IsDirectory = true }
        ]);
        var watches = new ScriptedWatchSource();

        using var directory = new TempDirectory("synthetic-index-source");
        var source = SyntheticIndexSource.Create(producer, watches);
        var path = Path.Combine(directory.Path, "scan.mlix");
        var result = await source.Producer(Request(path), CancellationToken.None);
        using var block = result.Block;

        Assert.AreSame(watches.IndexSource, source.WatchSource);
        Assert.AreSequenceEqual(['T'], [.. producer.ProducedVolumes]);
        Assert.IsGreaterThan(0u, block.Header.RowCount);
    }

    [TestMethod]
    public async Task Create_WithoutAProducerFailsEveryScanClearly()
    {
        using var directory = new TempDirectory("synthetic-index-source", create: false);
        var source = SyntheticIndexSource.Create();

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            source.Producer(Request(Path.Combine(directory.Path, "never-created.mlix")), CancellationToken.None));

        Assert.Contains("no synthetic producer was supplied", failure.Message);
        Assert.IsNull(source.WatchSource);
    }
}
