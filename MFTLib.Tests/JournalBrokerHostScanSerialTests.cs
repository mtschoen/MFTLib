using MFTLib.Index;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The serial the client stamped on its block travels with the scan request and is checked on the handle the scan parses.
// The check itself, on matching and mismatching handle queries, is LiveVolumeSourcesIncludeFreedTests; these tests
// cover that the broker host asks for it with the requested serial and reports a refusal.
[TestClass]
[DoNotParallelize]
public sealed class JournalBrokerHostScanSerialTests
{
    static readonly UsnJournalCursor Armed = new(7, 1000);

    [TestMethod]
    [DataRow(0xA1B2C3D4UL)]
    [DataRow(0x1111111122222222UL)]
    [DataRow(0UL)]
    public async Task ArmAndScan_AsksTheVolumeAccessToCheckTheRequestedSerialOnTheScanHandle(ulong serial)
    {
        var observed = new List<(VolumeKey Volume, ulong? Serial)>();
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => Armed,
            ScanVolume = (_, _, _, _, _, _) => [[BrokerHostFactory.Record(5, ".", 3)]],
            ScanOpened = (volume, expected) => observed.Add((volume, expected))
        });
        using var blockWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, blockWriter);

        var pipe = await harness.OpenScanChannelAsync('D', expectedVolumeSerial: serial);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual((new VolumeKey('D'), (ulong?)serial), Assert.ContainsSingle(observed));
        Assert.AreEqual(BrokerFrameKind.ScanCompleted, frames[^1].Kind);
    }

    [TestMethod]
    public async Task ArmAndScan_WhenTheSerialIsRefused_WritesTheErrorBeforeAnyRowIsWritten()
    {
        var host = BrokerHostFactory.Create(new()
        {
            QueryCursor = _ => Armed,
            ScanVolume = (_, _, _, _, _, _) => throw new AssertFailedException("A refused scan parsed rows."),
            ScanOpened = (volume, _) => throw new IOException($"Volume {volume} does not have the expected serial.")
        });
        using var blockWriter = new RecordingBlockSectionWriter();
        await using var harness = new HostChannelHarness(host, blockWriter);

        var pipe = await harness.OpenScanChannelAsync('C', expectedVolumeSerial: 5);
        var frames = await HostChannelHarness.ReadToEndAsync(pipe);

        Assert.AreEqual(BrokerFrameKind.Cursor, frames[0].Kind);
        var error = frames[^1];
        Assert.AreEqual(BrokerFrameKind.Error, error.Kind);
        Assert.AreEqual("Volume C does not have the expected serial.", error.Message);
        Assert.IsFalse(frames.Any(frame => frame.Kind is BrokerFrameKind.ScanReady or BrokerFrameKind.ScanCompleted));
    }
}
