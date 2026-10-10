using System.Buffers;
using System.Buffers.Binary;
using MFTLib.Tests.TestSupport;
using MFTLibTestExtensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MFTLib.Tests;

// The frame length limit holds on the write side too: a writer never emits a prefix the reader
// would refuse, and each oversized input has a defined outcome.
[TestClass]
public class BrokerFrameWriteLimitTests
{
    static readonly UsnJournalCursor Armed = new(7, 1000);
    static readonly NtfsVolumeInformation Volume = new(1024 * 1000, 1024);

    static string[] KeepList(int count) => Enumerable.Repeat(new string('n', 255), count).ToArray();

    [TestMethod]
    public void WriteArmAndScan_KeepListOverTheLimit_ThrowsBeforeWritingAnything()
    {
        var buffer = new ArrayBufferWriter<byte>();

        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            BrokerProtocol.WriteArmAndScan(buffer, "section", KeepList(32_700)));

        StringAssert.Contains(exception.Message, "frame limit");
        Assert.AreEqual(0, buffer.WrittenCount);
    }

    [TestMethod]
    public void WriteJournalBatch_OverTheLimit_ThrowsBeforeWritingAnything()
    {
        var entries = Enumerable.Range(0, 30_500)
            .Select(index => JournalEntries.Create((ulong)index, index, new string('e', 255))).ToArray();
        var buffer = new ArrayBufferWriter<byte>();

        Assert.ThrowsException<InvalidOperationException>(() =>
            BrokerProtocol.WriteJournalBatch(buffer, Armed, entries));

        Assert.AreEqual(0, buffer.WrittenCount);
    }

    [TestMethod]
    public void ArmAndScanFrameLength_MatchesTheWrittenFrame()
    {
        var names = new[] { ".git", "HEAD" };
        var buffer = new ArrayBufferWriter<byte>();
        BrokerProtocol.WriteArmAndScan(buffer, "section-C", names);

        Assert.AreEqual(BinaryPrimitives.ReadInt32LittleEndian(buffer.WrittenSpan),
            BrokerProtocol.ArmAndScanFrameLength("section-C", names));
    }

    [TestMethod]
    public void WriteError_MessageOverTheLimit_IsCutAndMarkedToFit()
    {
        var buffer = new ArrayBufferWriter<byte>();

        BrokerProtocol.WriteError(buffer, 3, new string('x', 8_388_604));

        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out var consumed);
        Assert.AreEqual(BrokerFrameKind.Error, frame.Kind);
        Assert.AreEqual(BrokerProtocol.MaximumMessageUnits, frame.RequireMessage().Length);
        Assert.IsTrue(frame.RequireMessage().EndsWith(BrokerProtocol.TruncationMarker, StringComparison.Ordinal));
        Assert.IsTrue(consumed < BrokerFrameStream.MaximumFrameLength);
    }

    [TestMethod]
    public void WriteStalled_MessageOverTheLimit_IsCutAndMarkedToFit()
    {
        var buffer = new ArrayBufferWriter<byte>();

        BrokerProtocol.WriteStalled(buffer, new string('x', 9_000_000));

        var frame = BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _);
        Assert.AreEqual(BrokerProtocol.MaximumMessageUnits, frame.RequireMessage().Length);
    }

    [TestMethod]
    public void WriteError_MessageWithinTheLimit_IsUnchanged()
    {
        var buffer = new ArrayBufferWriter<byte>();

        BrokerProtocol.WriteError(buffer, 3, "journal wrapped");

        Assert.AreEqual("journal wrapped", BrokerProtocol.ReadFrame(buffer.WrittenSpan, out _).RequireMessage());
    }

    [TestMethod]
    public async Task ScanDriveAsync_KeepListOverTheLimit_ThrowsArgumentExceptionAndTheProcessStaysUsable()
    {
        await using var broker = new InProcessBroker(CreateHost());
        var options = new BrokerScanOptions
        {
            DirectoryScanFileNames = KeepList(32_700)
        };

        var exception = await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            broker.Process.ScanDriveAsync('C', TestBlockSections.Target(), options, CancellationToken.None)
                .WaitAsync(HostChannelHarness.HangGuard));

        Assert.AreEqual("options", exception.ParamName);
        StringAssert.Contains(exception.Message, "frame limit");
        var volume = await broker.Process.QueryVolumeAsync('C', CancellationToken.None)
            .WaitAsync(HostChannelHarness.HangGuard);
        Assert.AreEqual(Volume.BytesPerFileRecordSegment, volume.BytesPerFileRecordSegment);
    }

    static JournalBrokerHost CreateHost()
    {
        return new JournalBrokerHost(
            new JournalBrokerHost.VolumeSources(
                _ => Armed,
                (_, _, _, _, _, _) => [[new MftRecord(5, 5, new MftRecordFields(3), ".")]],
                (_, since, _) => (Array.Empty<UsnJournalEntry>(), since),
                QueryVolumeInformation: _ => Volume),
            processorCount: 2);
    }
}
