using MFTLib.Index;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

[TestClass]
public class SyntheticIndexSourceTests
{
    static MftBlockProduceRequest Request(string blockPath) => new()
    {
        DriveLetter = 'T',
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

        var source = SyntheticIndexSource.Create(producer, watches);
        var path = Path.Combine(Path.GetTempPath(), "synthetic-index-source-" + Guid.NewGuid().ToString("N") + ".mlix");
        var result = await source.Producer(Request(path), CancellationToken.None);
        using var block = result.Block;

        Assert.AreSame(watches, source.WatchSource);
        CollectionAssert.AreEqual(new[] { 'T' }, producer.ProducedDrives.ToArray());
        Assert.IsTrue(block.Header.RowCount > 0);
    }

    [TestMethod]
    public async Task Create_WithoutAProducerFailsEveryScanClearly()
    {
        var source = SyntheticIndexSource.Create();

        var failure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            source.Producer(Request(Path.Combine(Path.GetTempPath(), "never-created.mlix")), CancellationToken.None));

        StringAssert.Contains(failure.Message, "no synthetic producer was supplied");
        Assert.IsNull(source.WatchSource);
    }
}
